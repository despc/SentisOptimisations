using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// A safe zone asks whether a grid is let in once for the grid's mechanical group in its frame's walk, not once for
    /// every grid of the group.
    ///
    /// Each frame a safe zone walks the entities in it (<c>MySafeZone.UpdateBeforeSimulation</c>) and asks for every one
    /// that is not static whether it is let in (<c>IsSafe</c>); for a grid that looks over its whole mechanical group, the
    /// owners and their factions of each grid of it. A base with its turrets and rotors in a zone is many grids of one
    /// group: every one of them looked over all the others, every frame. On the prod's base in the stand's copy (30.10.2026,
    /// SentisTests' safezone_contents): 33 grids of a group of 32 in the zone, about a thousand grids looked at a frame,
    /// 0.35-0.40 ms of the frame - 97% of its cluster, its grids themselves frozen. A grid's answer there is its group's:
    /// for a grid IsSafe reads only the group's grids and the zone's lists, which the walk does not change.
    ///
    /// The answer is kept for the group while the zone's own walk runs, and only there (the zone's other questions -
    /// damage, building - are the game's each time); a grid not let in is pushed out and its motors locked as before.
    ///
    /// A grid the freezer froze (<see cref="Freezer.FrozenUpdateGuard.FrozenMark"/>) is passed over in the walk as one
    /// let in: it does not move, and nobody is near it (the freezer freezes only far from the players). One that should
    /// be pushed out is, once it is thawed - when a player comes near.
    /// </summary>
    [PatchShim]
    public static class SafeZoneGroupCheck
    {
        // the walk now on this thread (with SentisClusters the zones of different clusters walk on their threads at once)
        [ThreadStatic] private static MySafeZone _walking;
        [ThreadStatic] private static Dictionary<object, bool> _answers;

        // Read by SentisTests: grids asked about in the walks, of them answered from their group's answer, and frozen
        // ones passed over.
        public static long Asked, Kept, Frozen;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("SafeZoneGroupCheck", ctx, c =>
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var update = typeof(MySafeZone).GetMethod("UpdateBeforeSimulation", any, null, Type.EmptyTypes, null)
                         ?? throw new MissingMethodException("MySafeZone.UpdateBeforeSimulation");
            var isSafe = typeof(MySafeZone).GetMethod("IsSafe", any, null, new[] { typeof(MyEntity) }, null)
                         ?? throw new MissingMethodException("MySafeZone.IsSafe(MyEntity)");
            c.GetPattern(update).Prefixes.Add(Own(nameof(WalkStart)));
            c.GetPattern(update).Suffixes.Add(Own(nameof(WalkEnd)));
            c.GetPattern(isSafe).Prefixes.Add(Own(nameof(IsSafePrefix)));
            c.GetPattern(isSafe).Suffixes.Add(Own(nameof(IsSafeSuffix)));
        });

        private static MethodInfo Own(string name) => typeof(SafeZoneGroupCheck).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        private static void WalkStart(MySafeZone __instance)
        {
            _walking = __instance;
            (_answers ?? (_answers = new Dictionary<object, bool>())).Clear();
        }

        private static void WalkEnd()
        {
            _walking = null;
            _answers?.Clear();
        }

        private static bool IsSafePrefix(MySafeZone __instance, MyEntity entity, ref bool __result)
        {
            if (!ReferenceEquals(_walking, __instance) || !(entity is MyCubeGrid grid)) return true;
            if ((grid.Flags & Freezer.FrozenUpdateGuard.FrozenMark) != 0)
            {
                System.Threading.Interlocked.Increment(ref Frozen);
                __result = true;
                return false;
            }
            System.Threading.Interlocked.Increment(ref Asked);
            var group = MyCubeGridGroups.Static.Mechanical.GetGroup(grid);
            if (group == null || !_answers.TryGetValue(group, out var answer)) return true;
            System.Threading.Interlocked.Increment(ref Kept);
            __result = answer;
            return false;
        }

        private static void IsSafeSuffix(MySafeZone __instance, MyEntity entity, bool __result)
        {
            // a frozen grid's "let in" is not its group's answer
            if (!ReferenceEquals(_walking, __instance) || !(entity is MyCubeGrid grid) || (grid.Flags & Freezer.FrozenUpdateGuard.FrozenMark) != 0) return;
            var group = MyCubeGridGroups.Static.Mechanical.GetGroup(grid);
            if (group != null) _answers[group] = __result;
        }
    }
}
