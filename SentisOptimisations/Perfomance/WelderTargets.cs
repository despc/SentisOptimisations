using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRageMath;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// What a welder has to look at on another grid: the blocks in its sphere that are not finished.
    ///
    /// Every activation - four a second for every welder - the game walks every cell of the target
    /// grid inside the tool's sphere, tests each block against it and hands all of them over, and
    /// the welder then throws away every finished one. A ship welding a big blueprint, or any tool
    /// with an enlarged radius, walks thousands of blocks four times a second to find the handful
    /// that need it: on the welding bench this was the biggest single cost on the game thread.
    ///
    /// So each welder keeps the blocks of each grid inside its sphere, found once, and after that
    /// only looks at the blocks the grid gained since - the grid reports each one - and hands over
    /// the unfinished ones. The grid's removals are counted as well, and when its block count stops
    /// adding up with what was reported, the reports are not trusted and everyone walks it again.
    /// The full walk is also redone when the tool moved against the grid, its radius changed, or,
    /// as a last safety net, once a minute - spread over the welders, so a ship's tools do not all
    /// pay for it in the same frame. The welder's own grid is left to the game, which keeps its
    /// own cache of it.
    /// </summary>
    [PatchShim]
    public static class WelderTargets
    {
        private const long RefreshFrames = 3600;
        private const double MovedM = 0.1;
        private const int MaxLog = 8192;

        /// <summary>
        /// How often a welder looks over every block it keeps, not just the unfinished ones: a block
        /// that gets damaged says nothing, and this is how long it can wait to be noticed.
        /// </summary>
        private const long SweepFrames = 60;

        private sealed class GridLog
        {
            public MyCubeGrid Grid;
            public long Base;                                   // how many were dropped from the front
            public readonly List<MySlimBlock> Added = new List<MySlimBlock>();
            public long Removed;
            public long CountAtStart;                           // grid.BlocksCount = CountAtStart + End - Removed
            public Action<MySlimBlock> OnAdded;
            public Action<MySlimBlock> OnRemoved;
            public Action<VRage.Game.Entity.MyEntity> OnClose;
            public long End => Base + Added.Count;

            /// <summary>If the grid's count no longer adds up, something changed it unreported.</summary>
            public void Verify()
            {
                if (Grid.BlocksCount == CountAtStart + End - Removed) return;
                Base += Added.Count;
                Added.Clear();
                Removed = 0;
                CountAtStart = Grid.BlocksCount - End;
                Resyncs++;
            }
        }

        private sealed class Reach
        {
            public MyCubeGrid Grid;
            public Vector3D LocalCenter;
            public double Radius;
            public long Seen;
            public long Frame;
            public long SweepFrame;
            public long RemovedSeen;
            public readonly List<MySlimBlock> Blocks = new List<MySlimBlock>();
            public readonly List<MySlimBlock> Unfinished = new List<MySlimBlock>();
        }

        private static readonly Dictionary<long, GridLog> Logs = new Dictionary<long, GridLog>();
        private static readonly Dictionary<(long welder, long grid), Reach> Reaches = new Dictionary<(long, long), Reach>();
        private static readonly HashSet<MySlimBlock> Scratch = new HashSet<MySlimBlock>();

        // the welder whose ActivateCommon is running, and the frame it started in
        private static MyShipWelder _welder;
        private static long _frame = -1;
        private static bool _inner;

        public static long FullScans;
        public static long CachedScans;
        public static long Resyncs;
        public static long PreviewsSkipped;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("WelderTargets", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var activateCommon = typeof(MyShipToolBase).GetMethod("ActivateCommon",
                BindingFlags.Instance | BindingFlags.NonPublic);
            // GetBlocksForTool is one line and gets inlined into ActivateCommon, where a patch on
            // it is never reached; the method it forwards to is the one to take.
            var forTool = typeof(MyCubeGrid).GetMethod(nameof(MyCubeGrid.GetBlocksInsideSphereInternal),
                BindingFlags.Instance | BindingFlags.Public);
            if (activateCommon == null) throw new MissingMethodException("MyShipToolBase.ActivateCommon");
            if (forTool == null) throw new MissingMethodException("MyCubeGrid.GetBlocksInsideSphereInternal");

            var pattern = ctx.GetPattern(activateCommon);
            pattern.Prefixes.Add(Method(nameof(ActivateCommonPrefix)));
            pattern.Suffixes.Add(Method(nameof(ActivateCommonSuffix)));
            ctx.GetPattern(forTool).Prefixes.Add(Method(nameof(BlocksInSpherePrefix)));
        }

        private static MethodInfo Method(string name) =>
            typeof(WelderTargets).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        private static void ActivateCommonPrefix(MyShipToolBase __instance)
        {
            _welder = SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.WelderTweaksEnabled
                ? __instance as MyShipWelder
                : null;
            _frame = MySession.Static.GameplayFrameCounter;
        }

        private static void ActivateCommonSuffix() => _welder = null;

        private static bool BlocksInSpherePrefix(MyCubeGrid __instance, ref BoundingSphereD sphere,
            HashSet<MySlimBlock> blocks)
        {
            var welder = _welder;
            if (welder == null || _inner || _frame != MySession.Static.GameplayFrameCounter ||
                __instance == welder.CubeGrid)
                return true;

            // A projector's preview is not something a welder welds - it has no physics, and the
            // projection is built by its own path (WelderOptimization.WeldProjections). The game
            // still handed every hologram block over as a target, walking the whole blueprint on
            // every activation, and those blocks then took the places of real unfinished ones among
            // the few a welder picks, and had their components pulled to the tool for nothing.
            if (__instance.Projector != null)
            {
                blocks.Clear();
                PreviewsSkipped++;
                return false;
            }

            try
            {
                Fill(welder, __instance, ref sphere, blocks);
                return false;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "welder target cache failed");
                Reaches.Clear();
                return true;
            }
        }

        private static void Fill(MyShipWelder welder, MyCubeGrid grid, ref BoundingSphereD sphere,
            HashSet<MySlimBlock> blocks)
        {
            var frame = MySession.Static.GameplayFrameCounter;
            var log = LogOf(grid);
            log.Verify();
            var localCenter = Vector3D.Transform(sphere.Center, grid.PositionComp.WorldMatrixNormalizedInv);

            var key = (welder.EntityId, grid.EntityId);
            Reach reach;
            if (!Reaches.TryGetValue(key, out reach))
            {
                if (Reaches.Count > 2048) Purge(frame);
                reach = new Reach();
                Reaches[key] = reach;
            }

            if (reach.Grid != grid || reach.Radius != sphere.Radius || reach.Seen < log.Base ||
                frame - reach.Frame > RefreshFrames + (welder.EntityId & 1023) ||
                Vector3D.DistanceSquared(reach.LocalCenter, localCenter) > MovedM * MovedM)
            {
                // the game's own walk, once
                _inner = true;
                try { grid.GetBlocksForTool(ref sphere, Scratch); }
                finally { _inner = false; }
                reach.Blocks.Clear();
                reach.Blocks.AddRange(Scratch);
                Scratch.Clear();
                reach.Grid = grid;
                reach.Radius = sphere.Radius;
                reach.LocalCenter = localCenter;
                reach.Frame = frame;
                reach.Seen = log.End;
                reach.SweepFrame = -SweepFrames;
                FullScans++;
            }
            else
            {
                if (reach.Seen < log.End)
                {
                    var localSphere = new BoundingSphere(localCenter, (float)sphere.Radius);
                    var aabb = BoundingBoxD.CreateFromSphere(sphere);
                    for (var i = (int)(reach.Seen - log.Base); i < log.Added.Count; i++)
                    {
                        var block = log.Added[i];
                        if (!InSphere(grid, block, ref localSphere, ref aabb)) continue;
                        reach.Blocks.Add(block);
                        if (!block.IsFullIntegrity) reach.Unfinished.Add(block);
                    }
                    reach.Seen = log.End;
                }
                CachedScans++;
            }

            // Once a second every kept block is looked at again - that is where damage shows up -
            // and in between only the ones known to be unfinished. Whether a block is still on the
            // grid is only asked when the grid has lost blocks since the last look.
            var removals = log.Removed != reach.RemovedSeen;
            if (frame - reach.SweepFrame >= SweepFrames)
            {
                reach.SweepFrame = frame;
                reach.RemovedSeen = log.Removed;
                reach.Unfinished.Clear();
                var all = reach.Blocks;
                for (var i = all.Count - 1; i >= 0; i--)
                {
                    var block = all[i];
                    if (Gone(grid, block, removals))
                    {
                        all[i] = all[all.Count - 1];
                        all.RemoveAt(all.Count - 1);
                        continue;
                    }
                    if (!block.IsFullIntegrity) reach.Unfinished.Add(block);
                }
                removals = false;
            }

            blocks.Clear();
            var list = reach.Unfinished;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var block = list[i];
                if (Gone(grid, block, removals) || block.IsFullIntegrity)
                {
                    list[i] = list[list.Count - 1];
                    list.RemoveAt(list.Count - 1);
                    continue;
                }
                blocks.Add(block);
            }
        }

        private static bool Gone(MyCubeGrid grid, MySlimBlock block, bool checkGrid) =>
            block.CubeGrid != grid || block.IsDestroyed || checkGrid && grid.GetCubeBlock(block.Position) != block;

        /// <summary>The same test the game makes in MyCubeGrid.AddBlockInSphere.</summary>
        private static bool InSphere(MyCubeGrid grid, MySlimBlock block, ref BoundingSphere localSphere, ref BoundingBoxD aabb)
        {
            if (block.CubeGrid != grid) return false;
            var half = grid.GridSize / 2f;
            if (!new BoundingBox(block.Min * grid.GridSize - half, block.Max * grid.GridSize + half).Intersects(localSphere))
                return false;
            if (!MyFakes.ENABLE_TRIANGLE_CHECK_FOR_SHIPTOOLS) return true;
            var fat = block.FatBlock;
            return fat == null || fat.BlockDefinition.IsAirTight == true || fat.GetIntersectionWithAABB(ref aabb);
        }

        private static GridLog LogOf(MyCubeGrid grid)
        {
            GridLog log;
            if (Logs.TryGetValue(grid.EntityId, out log) && log.Grid == grid) return log;
            if (log != null) Forget(log);

            log = new GridLog { Grid = grid, CountAtStart = grid.BlocksCount };
            var captured = log;
            log.OnAdded = block =>
            {
                if (captured.Added.Count >= MaxLog)
                {
                    // everyone behind this point walks the grid again
                    captured.Base += captured.Added.Count;
                    captured.Added.Clear();
                }
                captured.Added.Add(block);
            };
            log.OnRemoved = _ => captured.Removed++;
            log.OnClose = _ => Forget(captured);
            grid.OnBlockAdded += log.OnAdded;
            grid.OnBlockRemoved += log.OnRemoved;
            grid.OnMarkForClose += log.OnClose;
            Logs[grid.EntityId] = log;
            return log;
        }

        private static void Forget(GridLog log)
        {
            log.Grid.OnBlockAdded -= log.OnAdded;
            log.Grid.OnBlockRemoved -= log.OnRemoved;
            log.Grid.OnMarkForClose -= log.OnClose;
            GridLog current;
            if (Logs.TryGetValue(log.Grid.EntityId, out current) && current == log) Logs.Remove(log.Grid.EntityId);
            log.Added.Clear();
        }

        private static void Purge(long frame)
        {
            var stale = new List<(long, long)>();
            foreach (var pair in Reaches)
                if (frame - pair.Value.Frame > RefreshFrames * 4 || pair.Value.Grid == null || pair.Value.Grid.MarkedForClose)
                    stale.Add(pair.Key);
            foreach (var key in stale) Reaches.Remove(key);
        }
    }
}
