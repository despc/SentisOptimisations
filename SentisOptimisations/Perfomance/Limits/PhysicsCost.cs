using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Havok;
using NLog;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using VRage.Game.ModAPI;
using VRageMath;
using VRageMath.Spatial;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// What the physics step costs by grid group: the one count the physics guard (<see cref="SentisOptimisationsPlugin.PhysicsGuard"/>)
    /// punishes by and SentisWatcher draws (its "grids: physics", read through <see cref="Share"/>).
    ///
    /// Havok has no time of a grid, nor of a simulation island: the islands of a world go through one queue of jobs. A
    /// cluster of the game is a world of its own (<c>MyPhysics.Clusters</c>, an <see cref="HkWorld"/> each), and those
    /// can be timed: one frame in <see cref="MeasureGapFrames"/> at most, one active cluster is stepped alone with a
    /// stopwatch around it, and then the others together as the game steps them (<c>MyPhysics.StepWorldsParallel</c>:
    /// the same calls, <c>InitMtStep</c>, the jobs, <c>FinishMtStep</c>; the clusters the game would not step are left
    /// as they are). The cost is one more round of Havok's job queue and that one cluster's work not overlapping the
    /// others'. The cluster measured next is the one with the most time not seen: its last time by the frames since
    /// then, a new cluster first.
    ///
    /// A step is shared out by cluster - by each one's last measured time (scaled by how much more or less there is in
    /// it to do since: <see cref="Work"/>), or, for a cluster not measured yet, by its work at the rate the measured ones
    /// go - and within a cluster by the work of each grid group in it. A grid alone in its cluster gets its cluster's
    /// time less the cluster's own, the others an estimate within theirs. A sleeping or a static grid, and a cluster not stepped, cost nothing.
    /// A group goes by its grid of the most blocks.
    ///
    /// The work, in milliseconds of a step, is what the stand measured (the scenario <c>physics_calibration</c>, every
    /// case alone in a cluster of its own, 16.10.2026): a stepped cluster costs about 0.18 ms of its own, nobody's
    /// (<see cref="ClusterOwnWork"/>). A grid awake costs by the shapes of its collision (<see cref="ShapeWork"/>), not
    /// by its blocks, speed or turning: a solid cube of 1000 armour blocks is one shape and costs nothing at 50 m/s or
    /// 1.8 rad/s, nor does a hollow tube 285 m long (64 shapes); a lattice of 2112 shapes 0.18 ms, a box of 4000 slopes
    /// 0.46 ms, the armour of a long hollow carrier (2823 shapes) 0.65 ms, the whole carrier (9221) 1.8 ms - standing
    /// still - and asleep, nothing. The bodies of a grid's blocks (door leaves, connectors) are the grid's, little each
    /// (<see cref="BodyWork"/>: 92 leaves of hangar doors 0.07 ms), and so is a mechanical connection (fourteen pistons
    /// moving or not, 29 bodies: 0.14 ms; a rotor at 5 or 30 RPM 0.02 ms; a rover driving 0.15 ms). A grid awake against
    /// another grid 0.07-0.2 ms more; on the voxels by the ground under it (its shadow along the gravity) - a block 0.04 ms,
    /// a plate of 5 x 5 0.15 ms, a stick of 2 x 2 x 38 lying 0.45 ms, a cube of 10 x 10 x 10 0.35 ms, a slab of 20 x 2 x 20
    /// 1.2 ms (<see cref="VoxelTouchWork"/>, <see cref="VoxelAreaWork"/>; a wheel is a block); and on
    /// the voxels a long grid costs by its length and more as it moves (<see cref="VoxelLengthWork"/>,
    /// <see cref="VoxelSweepWork"/>: the voxels' shapes are made and collided over all of its box - in space or in the air
    /// the length costs nothing). A touch is known from the grid's contact callback
    /// (which the game already runs for every contact point of a grid's body): one in the last <see cref="TouchFrames"/>
    /// frames.
    /// </summary>
    [PatchShim]
    public static class PhysicsCost
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>At most one cluster's step timed in this many frames.</summary>
        public const int MeasureGapFrames = 30;

        /// <summary>A stepped cluster's own step, ms: the cluster's, nobody's.</summary>
        internal const double ClusterOwnWork = 0.18;

        /// <summary>An active body moving free, any body (a grid, a block's body, a character, a floating object), ms.</summary>
        internal const double BodyWork = 0.003;

        /// <summary>A shape of a grid's collision, of a grid awake, ms (a real carrier 0.0002, lattices of armour 0.0001).</summary>
        internal const double ShapeWork = 0.00015;

        /// <summary>A mechanical connection of a group (a rotor, a piston, a hinge, a wheel), ms.</summary>
        internal const double LinkWork = 0.004;

        /// <summary>A grid touching another grid, ms.</summary>
        internal const double GridTouchWork = 0.15;

        /// <summary>A grid touching the voxels, ms, and so much more for each square metre of the ground under it.</summary>
        internal const double VoxelTouchWork = 0.04, VoxelAreaWork = 0.00045;

        /// <summary>
        /// A grid on the voxels, more by its length: ms a metre of its longest side over <see cref="ShortLength"/> (a stick
        /// of 1250 m standing on its end 2.4 ms, 625 m lying 2 ms, 312 m 0.4 ms), and ms a metre of length a metre a second
        /// its ends sweep turning (its turning by half its length: a stick of 1250 m rolling on hills 30-90 ms, hitting the
        /// ground as it falls up to 27 ms). Its speed alone does not count: a long stick falling costs less than standing.
        /// </summary>
        internal const double VoxelLengthWork = 0.002, ShortLength = 25, VoxelSweepWork = 0.00005;

        /// <summary>A contact counts for so many frames after it (the callback comes for a body some frames, not all).</summary>
        private const ulong TouchFrames = 30;

        /// <summary>The last frame each grid touched the voxels, and another grid (written from Havok's threads).</summary>
        private static readonly ConcurrentDictionary<long, ulong> VoxelTouch = new ConcurrentDictionary<long, ulong>();
        private static readonly ConcurrentDictionary<long, ulong> GridTouch = new ConcurrentDictionary<long, ulong>();
        private static readonly List<long> Stale = new List<long>();

        /// <summary>A cluster's measurement not renewed this many frames (ten minutes) is forgotten.</summary>
        private const ulong ForgetFrames = 60UL * 600;

        private sealed class ClusterTime
        {
            public double Ms, Work;
            public ulong Frame;
            public int Count;
        }

        private static readonly Dictionary<long, long> GroupOf = new Dictionary<long, long>();
        private static readonly Dictionary<long, double> GroupWork = new Dictionary<long, double>();
        private static readonly Dictionary<long, int> GroupCluster = new Dictionary<long, int>();
        private static readonly Dictionary<int, double> ClusterWork = new Dictionary<int, double>();
        private static readonly Dictionary<int, double> ClusterBasis = new Dictionary<int, double>();
        private static readonly Dictionary<int, ClusterTime> ClusterTimes = new Dictionary<int, ClusterTime>();
        private static readonly List<MyClusterTree.MyCluster> Stepped = new List<MyClusterTree.MyCluster>();
        private static readonly List<int> Gone = new List<int>();

        private static Func<MyPhysics, int, int, bool> _clusterActive;
        private static FieldInfo _jobQueue, _threadPool;
        private static ulong _lastMeasured, _lastForgot;
        private static bool _measureBroken, _measuredThisFrame;

        /// <summary>For the stand's tests: the clusters' steps are timed (true unless a test turns it off).</summary>
        internal static bool ClusterTiming = true;

        /// <summary>For the stand's tests: clusters measured since the start.</summary>
        internal static long ClustersMeasured;

        /// <summary>For the stand's tests: the frames between two measurements (<see cref="MeasureGapFrames"/> unless a test sets it).</summary>
        internal static int GapFrames = MeasureGapFrames;

        /// <summary>For the stand's tests: each measurement as it is taken - the world and its step alone, in milliseconds (the game thread).</summary>
        internal static Action<HkWorld, double> Sampled;

        /// <summary>
        /// The physics step (MyPhysics.Simulate, from <see cref="PhysicsLoadMonitor"/>) on the frames with a cluster timed
        /// and on the others, ticks and frames - what the timing costs, for the stand's tests and the diagnostic log.
        /// </summary>
        internal static long StepTicksTimed, StepFramesTimed, StepTicksPlain, StepFramesPlain;

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("PhysicsCost", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var physics = typeof(MyPhysics);
            var active = physics.GetMethod("IsClusterActive", any, null, new[] { typeof(int), typeof(int) }, null)
                         ?? throw new MissingMethodException(nameof(MyPhysics), "IsClusterActive");
            _clusterActive = (Func<MyPhysics, int, int, bool>)Delegate.CreateDelegate(typeof(Func<MyPhysics, int, int, bool>), active);
            _jobQueue = physics.GetField("m_jobQueue", any) ?? throw new MissingFieldException(nameof(MyPhysics), "m_jobQueue");
            _threadPool = physics.GetField("m_threadPool", any) ?? throw new MissingFieldException(nameof(MyPhysics), "m_threadPool");
            if (_jobQueue.FieldType != typeof(HkJobQueue) || _threadPool.FieldType != typeof(HkJobThreadPool))
                throw new InvalidOperationException("MyPhysics' job queue is not what PhysicsCost knows");
            var step = physics.GetMethod("StepWorldsParallel", any, null, Type.EmptyTypes, null)
                       ?? throw new MissingMethodException(nameof(MyPhysics), "StepWorldsParallel");
            ctx.GetPattern(step).Prefixes.Add(typeof(PhysicsCost).GetMethod(nameof(StepWorldsPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            var contact = typeof(MyGridPhysics).GetMethod("RigidBody_ContactPointCallback", any, null, new[] { typeof(HkContactPointEvent).MakeByRefType() }, null)
                          ?? throw new MissingMethodException(nameof(MyGridPhysics), "RigidBody_ContactPointCallback");
            ctx.GetPattern(contact).Prefixes.Add(typeof(PhysicsCost).GetMethod(nameof(ContactPrefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>A contact point of a grid's body (Havok's threads): what it touches, by the frame.</summary>
        private static void ContactPrefix(MyGridPhysics __instance, ref HkContactPointEvent value)
        {
            try
            {
                if (!(__instance.Entity is MyCubeGrid grid)) return;
                var other = value.GetOtherEntity(grid);
                var frame = Sandbox.MySandboxGame.Static?.SimulationFrameCounter ?? 0UL;
                if (other is MyVoxelBase) VoxelTouch[grid.EntityId] = frame;
                else if (other is MyCubeGrid) GridTouch[grid.EntityId] = frame;
            }
            catch (Exception)
            {
                // a body leaving the world: this contact is not counted
            }
        }

        /// <summary>A grid body's work (see the class): free, against a grid, on the voxels by the ground under it.</summary>
        internal static double GridWork(MyCubeGrid grid, ulong frame)
        {
            var work = BodyWork + ShapeWork * ((grid.Physics as MyGridPhysics)?.Shape?.ShapeCount ?? 0);
            if (VoxelTouch.TryGetValue(grid.EntityId, out var voxels) && frame - voxels <= TouchFrames)
                work += VoxelWork(grid);
            else if (GridTouch.TryGetValue(grid.EntityId, out var grids) && frame - grids <= TouchFrames)
                work += GridTouchWork;
            return work;
        }

        /// <summary>For the stand's tests: a grid's work by its parts - shapes, touches, the ground under it.</summary>
        internal static string Describe(MyCubeGrid grid)
        {
            var frame = Sandbox.MySandboxGame.Static?.SimulationFrameCounter ?? 0UL;
            var shapes = (grid.Physics as MyGridPhysics)?.Shape?.ShapeCount ?? 0;
            var voxels = VoxelTouch.TryGetValue(grid.EntityId, out var v) && frame - v <= TouchFrames;
            var grids = GridTouch.TryGetValue(grid.EntityId, out var g) && frame - g <= TouchFrames;
            return $"awake {grid.Physics?.IsActive == true}, shapes {shapes} ({ShapeWork * shapes:0.000} ms)" +
                   (voxels ? $", on the voxels ({VoxelWork(grid):0.000} ms, ground {GroundArea(grid):0} m2)" : "") +
                   (!voxels && grids ? $", touching a grid ({GridTouchWork:0.000} ms)" : "") +
                   $", work {GridWork(grid, frame):0.000}";
        }

        /// <summary>
        /// The dynamic grid a body is of - its own body (<paramref name="own"/>) or a body of one of its blocks (a door's
        /// leaf, a connector) - or null for a body of nobody's (a character, a floating object, a static grid's).
        /// </summary>
        private static MyCubeGrid Owner(HkRigidBody body, out bool own)
        {
            own = false;
            if (!(body.UserObject is MyPhysicsBody physics) || physics.Entity == null) return null;
            own = physics.Entity is MyCubeGrid;
            if (!((own ? physics.Entity : physics.Entity.GetTopMostParent()) is MyCubeGrid grid)) return null;
            if (grid.MarkedForClose || grid.IsStatic || grid.Physics?.RigidBody == null || grid.Physics.RigidBody.GetMotionType() == HkMotionType.Fixed) return null;
            return grid;
        }

        /// <summary>A grid's work on the voxels: the ground under it, its length, what it sweeps.</summary>
        internal static double VoxelWork(MyCubeGrid grid)
        {
            var size = grid.PositionComp.LocalAABB.Size;
            var length = Math.Max(size.X, Math.Max(size.Y, size.Z));
            var w = grid.Physics?.AngularVelocity.Length() ?? 0;
            return VoxelTouchWork + VoxelAreaWork * GroundArea(grid) + VoxelLengthWork * Math.Max(0, length - ShortLength)
                   + VoxelSweepWork * length * (w * length / 2);
        }

        /// <summary>
        /// The square metres of the grid's box seen from below: its shadow along the gravity (each face by how much it faces
        /// down - a slab tilted a degree or two is still its whole underside); without gravity, its biggest face.
        /// </summary>
        internal static double GroundArea(MyCubeGrid grid)
        {
            var size = (Vector3D)(grid.Max - grid.Min + Vector3I.One) * grid.GridSize;
            var gravity = grid.Physics?.Gravity ?? Vector3.Zero;
            if (gravity.LengthSquared() < 1e-4f) return Math.Max(size.X * size.Y, Math.Max(size.Y * size.Z, size.X * size.Z));
            var down = Vector3D.TransformNormal((Vector3D)gravity, MatrixD.Transpose(grid.WorldMatrix));
            down.Normalize();
            return Math.Abs(down.X) * size.Y * size.Z + Math.Abs(down.Y) * size.X * size.Z + Math.Abs(down.Z) * size.X * size.Y;
        }

        /// <summary>Starts over on world load: the previous world's clusters are not this one's.</summary>
        public static void Reset()
        {
            ClusterTimes.Clear();
            _lastMeasured = _lastForgot = 0;
        }

        /// <summary>The step of a frame (from <see cref="PhysicsLoadMonitor"/>): counted against the frames with a cluster timed or not.</summary>
        internal static void CountStep(long ticks)
        {
            if (_measuredThisFrame)
            {
                StepTicksTimed += ticks;
                StepFramesTimed++;
            }
            else
            {
                StepTicksPlain += ticks;
                StepFramesPlain++;
            }
            _measuredThisFrame = false;
        }

        private static bool Active(MyPhysics physics, MyClusterTree.MyCluster cluster, out HkWorld world)
        {
            world = cluster.UserData as HkWorld;
            return world != null && _clusterActive(physics, cluster.ClusterId, world.CharacterRigidBodies.Count);
        }

        /// <summary>Now and then: one cluster stepped alone and timed, then the rest as the game does.</summary>
        private static bool StepWorldsPrefix(MyPhysics __instance)
        {
            if (!ClusterTiming || _measureBroken) return true;
            var frame = Sandbox.MySandboxGame.Static?.SimulationFrameCounter ?? 0UL;
            if (frame - _lastMeasured < (ulong)Math.Max(1, GapFrames)) return true;
            if (frame - _lastForgot > 3600) Forget(frame);

            var clusters = MyPhysics.Clusters.GetClusters();
            MyClusterTree.MyCluster pick = null;
            var best = -1.0;
            Stepped.Clear();
            foreach (var cluster in clusters)
            {
                if (!Active(__instance, cluster, out _)) continue;
                Stepped.Add(cluster);
                var priority = ClusterTimes.TryGetValue(cluster.ClusterId, out var seen)
                    ? (seen.Ms + 0.05) * (frame - seen.Frame)
                    : double.MaxValue;
                if (priority > best)
                {
                    best = priority;
                    pick = cluster;
                }
            }
            if (pick == null) return true;
            _lastMeasured = frame;

            var started = false;
            try
            {
                var queue = (HkJobQueue)_jobQueue.GetValue(null);
                var pool = (HkJobThreadPool)_threadPool.GetValue(null);
                var dt = 1f / 60f * MyFakes.SIMULATION_SPEED;
                using (MyEntities.StartAsyncUpdateBlock())
                using (HkAccessControl.PushState(HkAccessControl.AccessState.SharedRead))
                {
                    started = true;
                    var alone = (HkWorld)pick.UserData;
                    var start = Stopwatch.GetTimestamp();
                    queue.WaitPolicy = HkJobQueue.WaitPolicyT.WAIT_INDEFINITELY;
                    pool.ExecuteJobQueue(queue);
                    alone.ExecutePendingCriticalOperations();
                    alone.UnmarkForWrite();
                    alone.InitMtStep(queue, dt);
                    queue.WaitPolicy = HkJobQueue.WaitPolicyT.WAIT_UNTIL_ALL_WORK_COMPLETE;
                    queue.ProcessAllJobs();
                    pool.WaitForCompletion();
                    alone.FinishMtStep(queue, pool);
                    alone.MarkForWrite();
                    var ticks = Stopwatch.GetTimestamp() - start;
                    Measured(pick.ClusterId, alone, ticks * 1000.0 / Stopwatch.Frequency, frame);

                    if (Stepped.Count > 1)
                    {
                        queue.WaitPolicy = HkJobQueue.WaitPolicyT.WAIT_INDEFINITELY;
                        pool.ExecuteJobQueue(queue);
                        foreach (var cluster in Stepped)
                        {
                            if (cluster == pick) continue;
                            var world = (HkWorld)cluster.UserData;
                            world.ExecutePendingCriticalOperations();
                            world.UnmarkForWrite();
                            world.InitMtStep(queue, dt);
                        }
                        queue.WaitPolicy = HkJobQueue.WaitPolicyT.WAIT_UNTIL_ALL_WORK_COMPLETE;
                        queue.ProcessAllJobs();
                        pool.WaitForCompletion();
                        foreach (var cluster in Stepped)
                        {
                            if (cluster == pick) continue;
                            var world = (HkWorld)cluster.UserData;
                            world.FinishMtStep(queue, pool);
                            world.MarkForWrite();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // not tried again: the game's own step from the next frame on (this one's is not repeated when part of it ran)
                _measureBroken = true;
                Log.Error(e, "PhysicsCost: the timed step of a physics cluster failed; the clusters are not timed any more");
                return !started;
            }
            finally
            {
                Stepped.Clear();
            }
            ClustersMeasured++;
            _measuredThisFrame = true;
            return false;
        }

        private static void Measured(int clusterId, HkWorld world, double ms, ulong frame)
        {
            if (!ClusterTimes.TryGetValue(clusterId, out var seen)) ClusterTimes[clusterId] = seen = new ClusterTime();
            // a step's time jitters: half of it from the last ones
            seen.Ms = seen.Count == 0 ? ms : (seen.Ms + ms) / 2;
            seen.Work = Work(world);
            seen.Frame = frame;
            seen.Count++;
            try { Sampled?.Invoke(world, ms); }
            catch (Exception e) { Log.Warn(e, "PhysicsCost: a test's sample handler failed"); }
            if (global::SentisOptimisations.DiagLog.On)
                Log.Info($"PhysicsCost: physics cluster {clusterId} stepped alone in {ms:0.000} ms ({world.ActiveRigidBodies.Count} active bodies, work {seen.Work:0.0})");
        }

        /// <summary>The measurements of clusters that are no more, and once a minute what the timing costs (diagnostic).</summary>
        private static void Forget(ulong frame)
        {
            _lastForgot = frame;
            Gone.Clear();
            foreach (var pair in ClusterTimes)
                if (frame - pair.Value.Frame > ForgetFrames) Gone.Add(pair.Key);
            foreach (var id in Gone) ClusterTimes.Remove(id);
            foreach (var touches in new[] { VoxelTouch, GridTouch })
            {
                Stale.Clear();
                foreach (var pair in touches)
                    if (frame - pair.Value > 600) Stale.Add(pair.Key);
                foreach (var id in Stale) touches.TryRemove(id, out _);
            }
            if (global::SentisOptimisations.DiagLog.On && StepFramesTimed > 0 && StepFramesPlain > 0)
                Log.Info($"PhysicsCost: the physics step {StepTicksPlain * 1000.0 / Stopwatch.Frequency / StepFramesPlain:0.000} ms a frame, " +
                         $"{StepTicksTimed * 1000.0 / Stopwatch.Frequency / StepFramesTimed:0.000} ms with a cluster stepped alone ({StepFramesTimed} of {StepFramesTimed + StepFramesPlain} frames)");
        }

        /// <summary>What there is to do in a world, by the measure the step is shared out with (the grid groups' work and the other bodies).</summary>
        private static double Work(HkWorld world)
        {
            var work = ClusterOwnWork;
            var frame = Sandbox.MySandboxGame.Static?.SimulationFrameCounter ?? 0UL;
            HashSet<long> groups = null;
            foreach (var body in world.ActiveRigidBodies)
            {
                var grid = Owner(body, out var own);
                if (grid == null) { work += BodyWork; continue; }
                work += own ? GridWork(grid, frame) : BodyWork;
                var nodes = MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Mechanical).GetGroupNodes(grid);
                if (nodes == null || nodes.Count < 2) continue;
                var key = nodes[0].EntityId;
                if ((groups ?? (groups = new HashSet<long>())).Add(key)) work += LinkWork * (nodes.Count - 1);
            }
            return work;
        }

        /// <summary>
        /// Shares <paramref name="total"/> (milliseconds, ticks - any unit) out between the grid groups the step is spent on
        /// (see the class): <paramref name="into"/> is cleared and gets each group's part by the id of its grid of the most
        /// blocks. What the characters and other bodies cost is nobody's, so the parts add up to less than the whole. The
        /// game thread only. Returns the number of groups.
        /// </summary>
        public static int Share(double total, Dictionary<long, double> into)
        {
            into.Clear();
            GroupOf.Clear();
            GroupWork.Clear();
            GroupCluster.Clear();
            ClusterWork.Clear();
            ClusterBasis.Clear();
            var physicsInstance = Sandbox.Game.World.MySession.Static?.GetComponent<MyPhysics>();
            if (physicsInstance == null || _clusterActive == null) return 0;
            var frame = Sandbox.MySandboxGame.Static?.SimulationFrameCounter ?? 0UL;
            foreach (var cluster in MyPhysics.Clusters.GetClusters())
            {
                if (!Active(physicsInstance, cluster, out var world)) continue;
                var id = cluster.ClusterId;
                var work = ClusterOwnWork;
                foreach (var body in world.ActiveRigidBodies)
                {
                    var grid = Owner(body, out var own);
                    if (grid == null)
                    {
                        work += BodyWork;
                        continue;
                    }
                    var weight = own ? GridWork(grid, frame) : BodyWork;
                    if (!GroupOf.TryGetValue(grid.EntityId, out var key))
                    {
                        var nodes = MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Mechanical).GetGroupNodes(grid);
                        var biggest = grid;
                        if (nodes != null)
                            foreach (var node in nodes)
                                if (node.BlocksCount > biggest.BlocksCount) biggest = node;
                        key = biggest.EntityId;
                        var first = !GroupWork.ContainsKey(key);
                        if (nodes != null)
                            foreach (var node in nodes) GroupOf[node.EntityId] = key;
                        GroupOf[grid.EntityId] = key;
                        if (first)
                        {
                            // every rotor, piston or wheel of the group is a constraint the solver carries
                            var links = nodes != null ? LinkWork * Math.Max(0, nodes.Count - 1) : 0;
                            GroupWork[key] = links;
                            GroupCluster[key] = id;
                            work += links;
                        }
                    }
                    GroupWork[key] += weight;
                    work += weight;
                }
                if (work > 0) ClusterWork[id] = work;
            }
            if (ClusterWork.Count == 0) return 0;

            // the clusters measured: their time, scaled by their work now against then; the others at the measured ones' rate
            double measuredMs = 0, measuredWork = 0;
            foreach (var pair in ClusterWork)
            {
                if (!ClusterTimes.TryGetValue(pair.Key, out var seen) || seen.Work <= 0) continue;
                var ms = seen.Ms * Math.Max(0.25, Math.Min(4, pair.Value / seen.Work));
                ClusterBasis[pair.Key] = ms;
                measuredMs += ms;
                measuredWork += pair.Value;
            }
            // the work is in milliseconds already: a cluster not measured at the measured ones' rate, or at its own
            var rate = measuredWork > 0 ? measuredMs / measuredWork : 1;
            var sum = 0.0;
            foreach (var pair in ClusterWork)
            {
                if (!ClusterBasis.TryGetValue(pair.Key, out var basis)) ClusterBasis[pair.Key] = basis = pair.Value * rate;
                sum += basis;
            }
            if (sum <= 0) return 0;
            foreach (var pair in GroupWork)
            {
                if (pair.Value <= 0) continue;
                var cluster = GroupCluster[pair.Key];
                into[pair.Key] = total * (ClusterBasis[cluster] / sum) * (pair.Value / ClusterWork[cluster]);
            }
            return into.Count;
        }
    }
}
