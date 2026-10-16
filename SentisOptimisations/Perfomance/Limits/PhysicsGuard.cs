using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// Finds the grid group that loads the physics of the server and hands it to <see cref="Punisher"/>.
    ///
    /// Two stages. While the physics step fits into the budget nothing happens at all: the guard only
    /// reads the average kept by <see cref="Optimizer.Optimizations.PhysicsLoadMonitor"/> and returns.
    /// Once the step is over the alert threshold, that time is split between grid groups by
    /// <see cref="Optimizer.Optimizations.PhysicsCost"/> - the same count SentisWatcher draws: the clusters
    /// timed now and then, within a cluster by work (active bodies weighed by their blocks, mechanical
    /// connections) - and the groups over the thresholds are alerted or punished.
    ///
    /// The previous version punished the group with the largest <b>mass</b> in the heaviest cluster, which is close
    /// to unrelated: a battleship resting in space is cheaper than a small rig on fifty rotors. Bodies
    /// that sleep and clusters that are not stepped cost nothing, a group that has no active body cannot be
    /// blamed at all, and what the characters and other bodies cost is nobody's, so a group is never charged
    /// for more than its own share.
    /// </summary>
    public static class PhysicsGuard
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>How many groups over the alert threshold are reported in one check.</summary>
        private const int MaxGroupsPerCheck = 5;

        /// <summary>
        /// The checks are frequent so a short spike is not missed, but one group is only counted
        /// against this often: otherwise the escalation behind PhysicsChecksBeforePunish would run
        /// through in a few seconds instead of over a sustained load.
        /// </summary>
        private static readonly TimeSpan GroupCooldown = TimeSpan.FromSeconds(10);

        private static readonly Dictionary<long, DateTime> ActedAt = new Dictionary<long, DateTime>();

        /// <summary>
        /// Nothing is checked this long after the world is loaded: the first minute's physics is the world settling
        /// (grids placed, bodies waking, planets streamed in), not a grid of anybody's to blame.
        /// </summary>
        public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);

        private static DateTime? _loadedAt;

        /// <summary>The world is loaded: the checks start <see cref="StartDelay"/> from now.</summary>
        public static void OnWorldLoaded() => _loadedAt = DateTime.UtcNow;

        /// <summary>Whether the checks still wait for the world to settle (no load noted: they do not).</summary>
        public static bool Waiting(DateTime now, DateTime? loadedAt) => loadedAt.HasValue && now - loadedAt.Value < StartDelay;

        private static readonly Dictionary<long, double> Shares = new Dictionary<long, double>();
        private static readonly List<KeyValuePair<long, double>> Worst = new List<KeyValuePair<long, double>>();

        /// <summary>Called from the background loop; does nothing while the physics step is healthy.</summary>
        public static void Check()
        {
            if (!SentisOptimisationsPlugin.Config.EnablePhysicsGuard) return;
            if (Waiting(DateTime.UtcNow, _loadedAt)) return;
            if (Optimizer.Optimizations.PhysicsLoadMonitor.Frames == 0) return;

            var stepMs = Optimizer.Optimizations.PhysicsLoadMonitor.AverageMs;
            if (stepMs < SentisOptimisationsPlugin.Config.PhysicsMsToAlert) return;

            MyAPIGateway.Utilities.InvokeOnGameThread(() =>
            {
                try
                {
                    Analyse(stepMs);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Physics guard analysis failed");
                }
            });
        }

        /// <summary>Splits <paramref name="stepMs"/> between grid groups and acts on the worst ones.</summary>
        private static void Analyse(double stepMs)
        {
            if (Optimizer.Optimizations.PhysicsCost.Share(stepMs, Shares) == 0) return;
            Worst.Clear();
            Worst.AddRange(Shares);
            Worst.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (var i = 0; i < Worst.Count && i < MaxGroupsPerCheck; i++)
            {
                var groupMs = Worst[i].Value;
                if (groupMs < SentisOptimisationsPlugin.Config.PhysicsMsToAlert) break;
                var grids = GroupOf(Worst[i].Key);
                if (grids != null && !OnCooldown(Worst[i].Key)) Act(grids, groupMs);
            }
        }

        /// <summary>True while the group has been acted on too recently to be counted again.</summary>
        private static bool OnCooldown(long key)
        {
            var now = DateTime.UtcNow;
            if (ActedAt.TryGetValue(key, out var last) && now - last < GroupCooldown) return true;
            ActedAt[key] = now;
            if (ActedAt.Count > 256)
            {
                var stale = ActedAt.Where(pair => now - pair.Value > GroupCooldown).Select(pair => pair.Key).ToList();
                foreach (var old in stale) ActedAt.Remove(old);
            }
            return false;
        }

        private static void Act(List<IMyCubeGrid> grids, double groupMs)
        {
            if (groupMs > SentisOptimisationsPlugin.Config.PhysicsMsToPunishImmediately)
            {
                Punisher.__instance.PunishPlayerGridImmediately(grids);
                return;
            }

            if (groupMs > SentisOptimisationsPlugin.Config.PhysicsMsToPunish)
            {
                Punisher.__instance.PunishPlayerGrid(grids);
                return;
            }

            Punisher.__instance.AlertPlayerGrid(grids);
        }

        /// <summary>The mechanical group of the grid a share is kept by, or null when the grid is gone.</summary>
        private static List<IMyCubeGrid> GroupOf(long gridId)
        {
            if (!(MyEntities.GetEntityById(gridId) is MyCubeGrid grid) || grid.MarkedForClose) return null;
            var nodes = MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Mechanical).GetGroupNodes(grid);
            var grids = new List<IMyCubeGrid>();
            if (nodes == null || nodes.Count == 0) grids.Add(grid);
            else foreach (var node in nodes) grids.Add(node);
            return grids;
        }
    }
}
