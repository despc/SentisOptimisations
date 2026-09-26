using System;
using System.Collections.Generic;
using System.Linq;
using Havok;
using NLog;
using Sandbox.Engine.Physics;
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
    /// Once the step is over the alert threshold, one pass over the active rigid bodies of every
    /// cluster splits that time between grid groups, and the groups over the thresholds are alerted
    /// or punished.
    ///
    /// The share of a group is its share of the physics work, counted as <b>active rigid bodies plus
    /// mechanical connections</b> - what Havok actually integrates, collides and solves. The previous
    /// version punished the group with the largest <b>mass</b> in the heaviest cluster, which is close
    /// to unrelated: a battleship resting in space is cheaper than a small rig on fifty rotors. Bodies
    /// that sleep cost nothing and are not counted, and a group that has no active body cannot be
    /// blamed at all.
    ///
    /// The proxy has its limits - it does not see how many contact points a single body makes - so it
    /// is deliberately conservative: the denominator counts every active body in the world, including
    /// characters and floating objects, so a group is never charged for more than its own share.
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

        private sealed class GroupLoad
        {
            public long Key;
            public readonly List<IMyCubeGrid> Grids = new List<IMyCubeGrid>();
            public double Cost;
        }

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
            var groups = new Dictionary<long, GroupLoad>();
            var groupOfGrid = new Dictionary<long, long>();
            double totalCost = 0;

            foreach (var cluster in MyPhysics.Clusters.GetList())
            {
                if (!(cluster is HkWorld world)) continue;
                foreach (var body in world.ActiveRigidBodies)
                {
                    // Every active body costs the step, so every one of them is in the denominator.
                    totalCost++;
                    var grid = GridOf(body);
                    if (grid == null) continue;
                    var load = LoadOf(grid, groups, groupOfGrid);
                    if (load != null) load.Cost++;
                }
            }

            if (totalCost <= 0 || groups.Count == 0) return;

            var worst = new List<GroupLoad>(groups.Values);
            worst.Sort((a, b) => b.Cost.CompareTo(a.Cost));
            for (var i = 0; i < worst.Count && i < MaxGroupsPerCheck; i++)
            {
                var groupMs = stepMs * worst[i].Cost / totalCost;
                if (groupMs < SentisOptimisationsPlugin.Config.PhysicsMsToAlert) break;
                if (!OnCooldown(worst[i].Key)) Act(worst[i].Grids, groupMs);
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

        /// <summary>The dynamic grid a rigid body belongs to, or null for anything else.</summary>
        private static MyCubeGrid GridOf(HkRigidBody body)
        {
            if (!(body.UserObject is MyPhysicsBody physics)) return null;
            if (!(physics.Entity is MyCubeGrid grid)) return null;
            if (grid.MarkedForClose || grid.IsStatic || grid.Physics == null) return null;
            if (grid.Physics.RigidBody == null || grid.Physics.RigidBody.GetMotionType() == HkMotionType.Fixed) return null;
            return grid;
        }

        /// <summary>
        /// The load of the grid's mechanical group, created on first sight. The group is collected
        /// once per pass and its mechanical connections are counted in as work of their own: every
        /// rotor, piston or wheel is a constraint the solver carries.
        /// </summary>
        private static GroupLoad LoadOf(MyCubeGrid grid, Dictionary<long, GroupLoad> groups, Dictionary<long, long> groupOfGrid)
        {
            if (groupOfGrid.TryGetValue(grid.EntityId, out var known)) return groups[known];

            var nodes = MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Mechanical).GetGroupNodes(grid);
            if (nodes == null || nodes.Count == 0) nodes = new List<MyCubeGrid> { grid };

            var key = long.MaxValue;
            foreach (var node in nodes)
                if (node.EntityId < key) key = node.EntityId;

            if (!groups.TryGetValue(key, out var load))
            {
                load = new GroupLoad { Key = key, Cost = nodes.Count - 1 };
                foreach (var node in nodes)
                {
                    load.Grids.Add(node);
                    groupOfGrid[node.EntityId] = key;
                }
                groups[key] = load;
            }
            else
            {
                groupOfGrid[grid.EntityId] = key;
            }

            return load;
        }
    }
}
