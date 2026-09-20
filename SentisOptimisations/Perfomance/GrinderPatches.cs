using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Ship grinders (and welders, for the block sync) with less work per hit.
    ///
    /// Blade animation: a grinder activates at most every 250 ms, its UpdateAfterSimulation10 comes
    /// every ~167 ms, so every other call finds it throttled and it stops its blade animation, the next
    /// one starts it again: a render call per blade, per grinder, every 10 frames, on a server with no
    /// renderer. Skipped on a dedicated server.
    ///
    /// Block sync: every hit on a block raised an integrity event and one or two stockpile events to
    /// every client (MyCubeGrid.SendIntegrityChanged / SendStockpileChanged). Now a block sends at most
    /// once per <see cref="SyncWindowFrames"/> while it is being ground, welded or damaged: the
    /// integrity with its latest value, the stockpile changes summed. Begin/end of construction or
    /// deconstruction go at once (after what is pending). Pending sends for a block no longer at its
    /// place (razed, replaced) are dropped. Flushed every frame from the plugin's Update.
    ///
    /// Construction models: a block whose integrity drops below a build stage gets a fat block with
    /// that stage's model, and on a server the model is read from disk right there, in the frame:
    /// 7-14 ms the first time each model is needed (seen in freezer_stress... in grinder_perf worst
    /// frames). MyCubeBlockDefinition.PreloadConstructionModels only asks the renderer, which a
    /// dedicated server does not have, so the same call now also queues the stage models of that
    /// block onto a background thread, where MyModels loads them under its own lock.
    /// </summary>
    [PatchShim]
    public static class GrinderPatches
    {
        internal const int SyncWindowFrames = 15;

        private sealed class Pending
        {
            public MyCubeGrid Grid;
            public MySlimBlock Block;
            public ulong IntegritySentAt, StockpileSentAt;
            public bool HasIntegrity;
            public MyIntegrityChangeEnum IntegrityType;
            public long ToolOwner;
            public readonly Dictionary<MyDefinitionId, MyStockpileItem> Stockpile = new Dictionary<MyDefinitionId, MyStockpileItem>();
        }

        private static readonly System.Collections.Concurrent.BlockingCollection<Sandbox.Definitions.MyCubeBlockDefinition> ToPreload =
            new System.Collections.Concurrent.BlockingCollection<Sandbox.Definitions.MyCubeBlockDefinition>();
        private static readonly HashSet<MyDefinitionId> Preloading = new HashSet<MyDefinitionId>();
        private static readonly HashSet<long> PreloadedGrids = new HashSet<long>();
        private static System.Threading.Thread _preloadThread;

        private static readonly Dictionary<MySlimBlock, Pending> Blocks = new Dictionary<MySlimBlock, Pending>();
        private static readonly List<MySlimBlock> Done = new List<MySlimBlock>();
        private static readonly List<MyStockpileItem> SendList = new List<MyStockpileItem>();
        private static bool _bypass;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("GrinderPatches", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var skip = typeof(GrinderPatches).GetMethod(nameof(SkipOnDedicated), BindingFlags.Static | BindingFlags.NonPublic);
            ctx.GetPattern(typeof(MyShipGrinder).GetMethod("StartAnimation", any)).Prefixes.Add(skip);
            ctx.GetPattern(typeof(MyShipGrinder).GetMethod("StopAnimation", any)).Prefixes.Add(skip);
            ctx.GetPattern(typeof(MyCubeGrid).GetMethod(nameof(MyCubeGrid.SendIntegrityChanged), any)).Prefixes.Add(
                typeof(GrinderPatches).GetMethod(nameof(IntegrityPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            ctx.GetPattern(typeof(MyCubeGrid).GetMethod(nameof(MyCubeGrid.SendStockpileChanged), any)).Prefixes.Add(
                typeof(GrinderPatches).GetMethod(nameof(StockpilePrefix), BindingFlags.Static | BindingFlags.NonPublic));
            ctx.GetPattern(typeof(MyShipGrinder).GetMethod("Activate", any)).Prefixes.Add(
                typeof(GrinderPatches).GetMethod(nameof(ActivatePrefix), BindingFlags.Static | BindingFlags.NonPublic));
            ctx.GetPattern(typeof(Sandbox.Definitions.MyCubeBlockDefinition).GetMethod("PreloadConstructionModels",
                BindingFlags.Static | BindingFlags.Public)).Prefixes.Add(
                typeof(GrinderPatches).GetMethod(nameof(PreloadPrefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool SkipOnDedicated() => !Sandbox.Engine.Platform.Game.IsDedicated;

        private static ulong Frame => MySandboxGame.Static.SimulationFrameCounter;

        // Anything raised off the game thread goes out as vanilla sends it.
        private static bool OnGameThread => System.Threading.Thread.CurrentThread == MySandboxGame.Static.UpdateThread;

        private static bool IsProcess(MyIntegrityChangeEnum type) =>
            type == MyIntegrityChangeEnum.DeconstructionProcess || type == MyIntegrityChangeEnum.ConstructionProcess ||
            type == MyIntegrityChangeEnum.Damage || type == MyIntegrityChangeEnum.Repair;

        private static Pending Entry(MyCubeGrid grid, MySlimBlock block)
        {
            if (!Blocks.TryGetValue(block, out var entry))
            {
                entry = new Pending { Grid = grid, Block = block };
                Blocks[block] = entry;
            }
            return entry;
        }

        private static bool IntegrityPrefix(MyCubeGrid __instance, MySlimBlock mySlimBlock, MyIntegrityChangeEnum integrityChangeType, long toolOwner)
        {
            if (_bypass || mySlimBlock == null || !OnGameThread) return true;
            try
            {
                var frame = Frame;
                var entry = Entry(__instance, mySlimBlock);
                if (!IsProcess(integrityChangeType))
                {
                    // A begin or an end: what is pending first, then this one, now.
                    Send(entry, frame, force: true);
                    entry.IntegritySentAt = frame;
                    return true;
                }

                if (frame - entry.IntegritySentAt >= SyncWindowFrames && !entry.HasIntegrity)
                {
                    entry.IntegritySentAt = frame;
                    return true;
                }

                entry.HasIntegrity = true;
                entry.IntegrityType = integrityChangeType;
                entry.ToolOwner = toolOwner;
                return false;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "GrinderPatches integrity");
                return true;
            }
        }

        private static bool StockpilePrefix(MyCubeGrid __instance, MySlimBlock mySlimBlock, List<MyStockpileItem> list)
        {
            if (_bypass || mySlimBlock == null || list == null || list.Count == 0 || !OnGameThread) return true;
            try
            {
                var frame = Frame;
                var entry = Entry(__instance, mySlimBlock);
                if (frame - entry.StockpileSentAt >= SyncWindowFrames && entry.Stockpile.Count == 0)
                {
                    entry.StockpileSentAt = frame;
                    return true;
                }

                // The list is the stockpile's sync list, cleared after this call: keep a sum of it.
                foreach (var item in list)
                {
                    if (item.Content == null) continue;
                    var id = item.Content.GetId();
                    if (entry.Stockpile.TryGetValue(id, out var sum))
                    {
                        sum.Amount += item.Amount;
                        entry.Stockpile[id] = sum;
                    }
                    else entry.Stockpile[id] = item;
                }
                return false;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "GrinderPatches stockpile");
                return true;
            }
        }

        /// <summary>
        /// The moment a grid starts being ground, the build stage models of every block type on it go
        /// to the background loader: queueing them per block (PreloadPrefix) is often too late, the
        /// same hit already drops the block below its stage and the model is read in the frame.
        /// </summary>
        private static bool ActivatePrefix(HashSet<MySlimBlock> targets)
        {
            try
            {
                if (targets == null || targets.Count == 0 || !Sandbox.Engine.Platform.Game.IsDedicated) return true;
                var grid = targets.FirstElement()?.CubeGrid;
                if (grid == null) return true;
                lock (Preloading)
                {
                    if (!PreloadedGrids.Add(grid.EntityId)) return true;
                }
                foreach (var block in grid.CubeBlocks)
                    QueuePreload(block.BlockDefinition);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "GrinderPatches grid preload");
            }
            return true;
        }

        /// <summary>Queues the block's build stage models for the background loader. Vanilla still runs.</summary>
        private static bool PreloadPrefix(Sandbox.Definitions.MyCubeBlockDefinition block)
        {
            try
            {
                if (Sandbox.Engine.Platform.Game.IsDedicated) QueuePreload(block);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "GrinderPatches preload");
            }
            return true;
        }

        private static void QueuePreload(Sandbox.Definitions.MyCubeBlockDefinition block)
        {
            if (block?.BuildProgressModels == null || block.BuildProgressModels.Length == 0) return;
            lock (Preloading)
            {
                if (!Preloading.Add(block.Id)) return;
                if (_preloadThread == null)
                {
                    _preloadThread = new System.Threading.Thread(PreloadLoop)
                    {
                        IsBackground = true,
                        Name = "SO construction models",
                        Priority = System.Threading.ThreadPriority.BelowNormal,
                    };
                    _preloadThread.Start();
                }
            }
            ToPreload.Add(block);
        }

        private static void PreloadLoop()
        {
            foreach (var block in ToPreload.GetConsumingEnumerable())
            {
                try
                {
                    foreach (var stage in block.BuildProgressModels)
                        if (!string.IsNullOrEmpty(stage?.File))
                            VRage.Game.Models.MyModels.GetModelOnlyData(stage.File);
                }
                catch (Exception e)
                {
                    SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "GrinderPatches preload of " + block.Id);
                }
            }
        }

        /// <summary>Sends what waited its window out and forgets quiet blocks. Game thread, every frame.</summary>
        public static void Flush()
        {
            if (Blocks.Count == 0) return;
            try
            {
                var frame = Frame;
                foreach (var entry in Blocks.Values)
                {
                    Send(entry, frame, force: false);
                    if (!entry.HasIntegrity && entry.Stockpile.Count == 0 &&
                        frame - entry.IntegritySentAt >= SyncWindowFrames && frame - entry.StockpileSentAt >= SyncWindowFrames)
                        Done.Add(entry.Block);
                }
                foreach (var block in Done) Blocks.Remove(block);
                Done.Clear();
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "GrinderPatches flush");
                Blocks.Clear();
                Done.Clear();
            }
        }

        private static void Send(Pending entry, ulong frame, bool force)
        {
            var grid = entry.Grid;
            var block = entry.Block;
            var here = grid != null && !grid.Closed && !grid.MarkedForClose && grid.GetCubeBlock(block.Position) == block;
            if (!here)
            {
                entry.HasIntegrity = false;
                entry.Stockpile.Clear();
                return;
            }

            _bypass = true;
            try
            {
                if (entry.Stockpile.Count > 0 && (force || frame - entry.StockpileSentAt >= SyncWindowFrames))
                {
                    SendList.Clear();
                    foreach (var item in entry.Stockpile.Values)
                        if (item.Amount != 0) SendList.Add(item);
                    entry.Stockpile.Clear();
                    entry.StockpileSentAt = frame;
                    if (SendList.Count > 0) grid.SendStockpileChanged(block, SendList);
                    SendList.Clear();
                }

                if (entry.HasIntegrity && (force || frame - entry.IntegritySentAt >= SyncWindowFrames))
                {
                    entry.HasIntegrity = false;
                    entry.IntegritySentAt = frame;
                    grid.SendIntegrityChanged(block, entry.IntegrityType, entry.ToolOwner);
                }
            }
            finally
            {
                _bypass = false;
            }
        }

        internal static void ClearAll()
        {
            Blocks.Clear();
            Done.Clear();
            lock (Preloading) PreloadedGrids.Clear();
        }
    }
}
