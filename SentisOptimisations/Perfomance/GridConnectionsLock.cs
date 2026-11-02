using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Sandbox.Game.EntityComponents;
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
    /// </summary>
    [PatchShim]
    public static class GridConnectionsLock
    {
        private static readonly MethodInfo Update = typeof(MyGridTargeting).GetMethod(nameof(MyGridTargeting.UpdateGridConnections),
            BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(List<List<long>>), typeof(List<long>), typeof(MyEntity) }, null);

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("GridConnectionsLock", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            if (Update == null) throw new MissingMethodException("MyGridTargeting.UpdateGridConnections");
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
                targeting.UpdateGridConnections(gridGroups, gridsGroupsForSuggested, suggestedEntity);
        }
    }
}
