using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Interfaces;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SentisOptimisationsPlugin;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using VRage.Game.ModAPI.Ingame;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// A turret with nothing to shoot at does not start a search that can only find nothing.
    ///
    /// Every turret on a grid a player sees starts a target search every 10 frames
    /// (<c>MyLargeTurretTargetingSystem.CheckAndSelectNearTargetsParallel</c>): on the game thread it
    /// copies every grid group within its reach (<c>MyGridTargeting.UpdateGridConnections</c>) and hands
    /// a task to the worker threads. 2560 interior turrets on 128 Spitfires paid 5 ms a frame for it on
    /// the stand, 20 us a search.
    ///
    /// The search looks only at the grid's target roots - what its last scan found within the turret
    /// range that the turret may shoot: enemy (neutral, friendly - as the turret is set) grids, and every
    /// character and missile. With no current target, no focused grid and not one such root, it ends
    /// with no target, which is what the turret already has; that search is skipped. A scan older than
    /// the game's 100 frames is never trusted: then the search runs as in vanilla, and rescans.
    /// </summary>
    [PatchShim]
    public static class TurretIdleSearch
    {
        private const int ScanFrames = 100;     // MyGridTargeting.RescanIfNeeded

        private static readonly Func<MyLargeTurretTargetingSystem, IMyTargetingReceiver> Receiver =
            Accessors.Field<MyLargeTurretTargetingSystem, IMyTargetingReceiver>("m_targetReceiver");
        private static readonly Func<MyLargeTurretTargetingSystem, MyCubeGrid> Focused =
            Accessors.Field<MyLargeTurretTargetingSystem, MyCubeGrid>("m_focusedTarget");
        private static readonly Func<MyGridTargeting, int> LastScan =
            Accessors.Field<MyGridTargeting, int>("m_lastScan");
        private static readonly Func<MyGridTargeting, List<MyEntity>> Roots =
            Accessors.Field<MyGridTargeting, List<MyEntity>>("m_targetRoots");
        private static readonly Func<MyGridTargeting, Dictionary<MyCubeGrid, MyGridTargetingRelationFiltering>> Relations =
            Accessors.Field<MyGridTargeting, Dictionary<MyCubeGrid, MyGridTargetingRelationFiltering>>("m_gridToRelation");

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("TurretIdleSearch", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var search = typeof(MyLargeTurretTargetingSystem).GetMethod(
                nameof(MyLargeTurretTargetingSystem.CheckAndSelectNearTargetsParallel),
                BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (search == null) throw new MissingMethodException("MyLargeTurretTargetingSystem.CheckAndSelectNearTargetsParallel");
            ctx.GetPattern(search).Prefixes.Add(typeof(TurretIdleSearch).GetMethod(nameof(SearchPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool SearchPrefix(MyLargeTurretTargetingSystem __instance)
        {
            try
            {
                return !NothingToFind(__instance);
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>True when the search could only end with no target, as the turret has now.</summary>
        internal static bool NothingToFind(MyLargeTurretTargetingSystem system)
        {
            if (system.ParallelTargetSelectionInProcess || system.Target != null || Focused(system) != null) return false;
            var receiver = Receiver(system);
            var grid = receiver?.GridTargeting;
            if (grid == null || MySession.Static == null) return false;
            if (MySession.Static.GameplayFrameCounter - LastScan(grid) > ScanFrames) return false;

            var enemies = receiver.TargetEnemies;
            var neutrals = receiver.TargetNeutrals;
            var friends = receiver.TargetFriends;
            var relations = Relations(grid);
            var scanLock = grid.ScanLock;
            scanLock.AcquireShared();
            try
            {
                // The test of MyGridTargeting.GetTargetRoots, without its rescan: a grid counts when its
                // relation is one the turret shoots at, anything else (a character, a missile) always.
                foreach (var root in Roots(grid))
                {
                    if (!(root is MyCubeGrid rootGrid)) return false;
                    if (relations.TryGetValue(rootGrid, out var relation) && CanTarget(enemies, neutrals, friends, relation))
                        return false;
                }
            }
            finally
            {
                scanLock.ReleaseShared();
            }
            return true;
        }

        /// <summary>MyGridTargeting.CanTarget.</summary>
        private static bool CanTarget(bool enemies, bool neutrals, bool friends, MyGridTargetingRelationFiltering filtering) =>
            (neutrals && (filtering & MyGridTargetingRelationFiltering.Neutral) == MyGridTargetingRelationFiltering.Neutral) ||
            (enemies && (filtering & MyGridTargetingRelationFiltering.Enemy) == MyGridTargetingRelationFiltering.Enemy) ||
            (friends && (filtering & MyGridTargetingRelationFiltering.Friend) == MyGridTargetingRelationFiltering.Friend);
    }
}
