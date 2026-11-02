using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Linq.Expressions;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using Sandbox.Game.Weapons;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using VRage.Game.Entity;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// A grid's list of the grid groups around it rebuilt by one turret at a time.
    ///
    /// From prod (02.11.2026, SentisClusters): a searchlight's target search threw "Collection was modified" in
    /// <c>MyGridTargeting.UpdateGridConnections</c> in a cluster's thread. The method rebuilds the grid's own
    /// <c>m_targetConnections</c> once a frame and then walks it; every turret, searchlight and turret controller of
    /// the grid calls it (<c>MyLargeTurretTargetingSystem.CheckAndSelectNearTargetsParallel</c>, its only caller),
    /// and here two threads did at once. Here that call takes the grid's targeting component's lock, in a
    /// try/finally: whatever the second thread was, the list is never rebuilt while it is walked.
    ///
    /// And the list is rebuilt at most every <see cref="KeepFrames"/> frames a grid, not every frame. Rebuilding it
    /// (<c>RefreshGridConnections</c>) takes every entity in a sphere of the turrets' range around the grid and walks the
    /// physical group of every grid among them. The game keeps it for the frame it was made in, for the grid's other
    /// turrets searching in the same frame; but TurretControlTargetSelect spreads a grid's searches over the frames, one
    /// block's phase each, so nearly every search rebuilt it: 398 ms of 35 s on the server (dotTrace 02.11.2026 17:42),
    /// most of what Watcher counted as SentisOptimisations. Within <see cref="KeepFrames"/> of its last rebuild the
    /// game is told the list is from this frame (<c>m_gridGroupCache</c>). A grid that came into range or joined
    /// another is in it at most that much later; a turret's own list of targets is looked over every 100 frames
    /// (<c>RescanIfNeeded</c>), one with a target searches every 10.
    /// </summary>
    [PatchShim]
    public static class GridConnectionsLock
    {
        private static readonly MethodInfo Update = typeof(MyGridTargeting).GetMethod(nameof(MyGridTargeting.UpdateGridConnections),
            BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(List<List<long>>), typeof(List<long>), typeof(MyEntity) }, null);

        /// <summary>How many frames a grid's list of the grid groups around it is used before it is made anew.</summary>
        public const int KeepFrames = 10;

        private static readonly FieldInfo CacheField = typeof(MyGridTargeting).GetField("m_gridGroupCache", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Func<MyGridTargeting, int> GetCache = CacheField == null ? null : Getter();
        private static readonly Action<MyGridTargeting, int> SetCache = CacheField == null ? null : Setter();

        private sealed class Made
        {
            public int Frame = int.MinValue;
        }

        /// <summary>The frame each grid's list was really made in (the game's own field is set ahead while it is kept).</summary>
        private static readonly ConditionalWeakTable<MyGridTargeting, Made> MadeAt = new ConditionalWeakTable<MyGridTargeting, Made>();

        /// <summary>Lists kept (not made anew) and made, since the start.</summary>
        public static long Kept, Rebuilt;

        private static Func<MyGridTargeting, int> Getter()
        {
            var arg = Expression.Parameter(typeof(MyGridTargeting), "t");
            return Expression.Lambda<Func<MyGridTargeting, int>>(Expression.Field(arg, CacheField), arg).Compile();
        }

        private static Action<MyGridTargeting, int> Setter()
        {
            var arg = Expression.Parameter(typeof(MyGridTargeting), "t");
            var value = Expression.Parameter(typeof(int), "v");
            return Expression.Lambda<Action<MyGridTargeting, int>>(Expression.Assign(Expression.Field(arg, CacheField), value), arg, value).Compile();
        }

        /// <summary>Whether a list made in frame <paramref name="made"/> is still used in frame <paramref name="now"/>.</summary>
        public static bool Fresh(int now, int made) => made != int.MinValue && now >= made && now - made < KeepFrames;

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("GridConnectionsLock", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            if (Update == null) throw new MissingMethodException("MyGridTargeting.UpdateGridConnections");
            if (CacheField == null || CacheField.FieldType != typeof(int)) throw new MissingFieldException("MyGridTargeting", "m_gridGroupCache");
            var search = typeof(MyLargeTurretTargetingSystem).GetMethod(nameof(MyLargeTurretTargetingSystem.CheckAndSelectNearTargetsParallel),
                BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)
                ?? throw new MissingMethodException("MyLargeTurretTargetingSystem.CheckAndSelectNearTargetsParallel");
            ctx.GetPattern(search).Transpilers.Add(typeof(GridConnectionsLock).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic));
        }

        internal static IEnumerable<MsilInstruction> Transpiler(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            var locked = typeof(GridConnectionsLock).GetMethod(nameof(UpdateLocked), BindingFlags.Static | BindingFlags.Public);
            var replaced = 0;
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].OpCode != OpCodes.Call && list[i].OpCode != OpCodes.Callvirt) continue;
                if (!(list[i].Operand is MsilOperandInline<MethodBase> operand) || operand.Value != Update) continue;
                // the same stack (the component, then its three arguments) into a static call
                var call = new MsilInstruction(OpCodes.Call).InlineValue((MethodBase)locked);
                foreach (var label in list[i].Labels) call.Labels.Add(label);
                list[i] = call;
                replaced++;
            }
            if (replaced != 1)
                throw new InvalidOperationException("GridConnectionsLock: expected 1 call of UpdateGridConnections, found " + replaced);
            return list;
        }

        /// <summary><c>targeting.UpdateGridConnections(...)</c> under the component's lock.</summary>
        public static void UpdateLocked(MyGridTargeting targeting, List<List<long>> gridGroups, List<long> gridsGroupsForSuggested, MyEntity suggestedEntity)
        {
            if (targeting == null)
            {
                targeting.UpdateGridConnections(gridGroups, gridsGroupsForSuggested, suggestedEntity); // the game's own NullReferenceException
                return;
            }
            lock (targeting)
            {
                var session = MySession.Static;
                var made = session == null ? null : MadeAt.GetOrCreateValue(targeting);
                var now = session?.GameplayFrameCounter ?? 0;
                var keep = made != null && Fresh(now, made.Frame);
                // the game makes the list anew when its field is not this frame: kept, the field is moved to this frame
                if (keep && GetCache(targeting) != now) SetCache(targeting, now);
                targeting.UpdateGridConnections(gridGroups, gridsGroupsForSuggested, suggestedEntity);
                if (made == null) return;
                if (keep) global::SentisOptimisations.ClusterThreads.Increment(ref Kept);
                else if (made.Frame != now)
                {
                    made.Frame = now;
                    global::SentisOptimisations.ClusterThreads.Increment(ref Rebuilt);
                }
            }
        }
    }
}
