using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Screens.Helpers;
using Torch.API;
using Torch.API.Session;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;

namespace SentisOptimisationsPlugin.Freezer
{
    /// <summary>
    /// Builds the save snapshot of frozen grids over several frames before the save itself.
    ///
    /// A save snapshot is taken on the game thread in one frame. Frozen grids do not update and players
    /// are far away, so their object builders do not change while they stay frozen and can be produced
    /// ahead of time. When a save is requested (vanilla MyAsyncSaving.Start: autosave, /save, scripts;
    /// Torch TorchAsyncSaving: !save) and the freezer has frozen grids, the request is held back; at the
    /// end of each frame frozen grids get BeforeSave + GetObjectBuilder within a small time budget, and
    /// once all are built the held requests run for real. ParallelEntitySave then takes the prepared
    /// builders and only builds the remaining grids in the snapshot frame.
    ///
    /// A prepared builder is dropped - and the grid built normally in the snapshot frame - if the grid
    /// is no longer frozen, was unfrozen in between (wake-up), closed, gained or lost a block, or an
    /// inventory on it changed. The synchronous save during world unload is not held back.
    ///
    /// "Frozen" is not fully immutable, measured on the save_perf fixture: timers keep ticking for a
    /// moment right after a grid is frozen, so only grids frozen for at least
    /// <see cref="MinFrozenFrames"/> are prepared; and the presence tiers of a grid change while it is
    /// frozen, so they are copied from the grid when the prepared builder is used.
    /// </summary>
    [PatchShim]
    public static class FrozenGridSaveCache
    {
        private const double FrameBudgetMs = 3.0;
        // Safety net: if collection takes longer than this, the held saves run with what is ready.
        private const int MaxCollectionFrames = 1800;
        // Prepared builders wait this long for the snapshot of a released save before being dropped.
        private const int MaxWaitForSnapshotFrames = 300;
        private const ulong MinFrozenFrames = 300;

        private sealed class Entry
        {
            public MyCubeGrid Grid;
            public MyObjectBuilder_EntityBase Builder;
            public bool Stale;
            public Action<MySlimBlock> BlockChanged;
            public Action<MyEntity> Closing;
        }

        private static readonly ConcurrentQueue<Action> HeldSaves = new ConcurrentQueue<Action>();
        private static readonly Dictionary<long, Entry> Entries = new Dictionary<long, Entry>();
        private static List<MyCubeGrid> _toCollect;
        private static int _collectIndex;
        private static int _collectionFrames;
        private static double _collectionMaxFrameMs;
        private static bool _collecting;
        private static int _waitForSnapshotFrames;
        [ThreadStatic] private static bool _bypass;

        // Statistics of the last completed save, read by tests.
        public static int LastCollectedGrids;
        public static int LastCollectionFrames;
        public static double LastCollectionMaxFrameMs;
        public static int LastSnapshotPreparedGrids;
        public static int LastSnapshotStaleGrids;
        public static HashSet<long> LastSnapshotBuiltGridIds = new HashSet<long>();

        // Test hook: compare every used prepared builder with a fresh one (serialized XML). Expensive.
        public static bool VerifyPreparedBuilders;
        public static int LastVerifyMismatches;
        public static string LastVerifyFirstDifference;

        private static MethodInfo _torchSaveInternal;

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("FrozenGridSaveCache", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags anyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            ctx.GetPattern(typeof(MyAsyncSaving).GetMethod(nameof(MyAsyncSaving.Start), anyStatic))
                .Prefixes.Add(typeof(FrozenGridSaveCache).GetMethod(nameof(AsyncSavingStartPrefix), anyStatic));

            var torchSaving = typeof(ITorchBase).Assembly.GetType("Torch.Patches.TorchAsyncSaving")
                              ?? AppDomain.CurrentDomain.GetAssemblies()
                                  .Select(a => a.GetType("Torch.Patches.TorchAsyncSaving", false))
                                  .FirstOrDefault(t => t != null);
            _torchSaveInternal = torchSaving?.GetMethod("SaveInternal", anyStatic);
            if (_torchSaveInternal != null)
                ctx.GetPattern(_torchSaveInternal).Prefixes.Add(
                    typeof(FrozenGridSaveCache).GetMethod(nameof(TorchSaveInternalPrefix), anyStatic));

            ctx.GetPattern(typeof(MySandboxGame).GetMethod("Update",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null))
                .Suffixes.Add(typeof(FrozenGridSaveCache).GetMethod(nameof(FrameSuffix), anyStatic));

            ctx.GetPattern(typeof(MyInventoryBase).GetMethod(nameof(MyInventoryBase.RaiseInventoryContentChanged),
                    BindingFlags.Instance | BindingFlags.Public))
                .Suffixes.Add(typeof(FrozenGridSaveCache).GetMethod(nameof(InventoryChangedSuffix), anyStatic));
        }

        private static bool ShouldHold()
        {
            return !_bypass && SentisOptimisationsPlugin.Config.FreezerEnabled && FreezeLogic.FrozenGrids.Count > 0;
        }

        private static bool AsyncSavingStartPrefix(Action callbackOnFinished, string customName)
        {
            if (!ShouldHold()) return true;
            HeldSaves.Enqueue(() =>
            {
                _bypass = true;
                try { MyAsyncSaving.Start(callbackOnFinished, customName); }
                finally { _bypass = false; }
            });
            return false;
        }

        private static bool TorchSaveInternalPrefix(ITorchBase torch, string newSaveName, ref Task<GameSaveResult> __result)
        {
            if (!ShouldHold()) return true;
            var completion = new TaskCompletionSource<GameSaveResult>();
            HeldSaves.Enqueue(() =>
            {
                Task<GameSaveResult> save;
                _bypass = true;
                try { save = (Task<GameSaveResult>)_torchSaveInternal.Invoke(null, new object[] { torch, newSaveName }); }
                catch (Exception e)
                {
                    completion.TrySetException(e);
                    return;
                }
                finally { _bypass = false; }
                save.ContinueWith(t =>
                {
                    if (t.IsFaulted) completion.TrySetException(t.Exception.InnerExceptions);
                    else if (t.IsCanceled) completion.TrySetCanceled();
                    else completion.TrySetResult(t.Result);
                });
            });
            __result = completion.Task;
            return false;
        }

        private static void FrameSuffix()
        {
            try
            {
                if (!_collecting)
                {
                    if (_waitForSnapshotFrames > 0 && --_waitForSnapshotFrames == 0) Clear();
                    if (HeldSaves.IsEmpty) return;
                    StartCollection();
                }
                CollectSome();
                if (_collectIndex < _toCollect.Count && _collectionFrames < MaxCollectionFrames) return;

                LastCollectedGrids = Entries.Count;
                LastCollectionFrames = _collectionFrames;
                LastCollectionMaxFrameMs = _collectionMaxFrameMs;
                _collecting = false;
                _toCollect = null;
                // Torch saves snapshot from its invoke queue a frame later; keep the builders until
                // MyEntities.Save consumes them, or drop them if no snapshot happens.
                _waitForSnapshotFrames = MaxWaitForSnapshotFrames;
                while (HeldSaves.TryDequeue(out var save)) save();
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "FrozenGridSaveCache failed; running held saves without prepared grids");
                _collecting = false;
                _toCollect = null;
                Clear();
                while (HeldSaves.TryDequeue(out var save)) save();
            }
        }

        private static void StartCollection()
        {
            Clear();
            _toCollect = new List<MyCubeGrid>();
            var frame = MySandboxGame.Static.SimulationFrameCounter;
            foreach (var id in FreezeLogic.FrozenGrids)
            {
                if (!FreezeLogic.FrozenAtFrame.TryGetValue(id, out var frozenAt) || frame - frozenAt < MinFrozenFrames) continue;
                if (MyEntities.TryGetEntityById(id, out MyCubeGrid grid) && grid.Save && !grid.MarkedForClose && !grid.Closed)
                    _toCollect.Add(grid);
            }
            _collectIndex = 0;
            _collectionFrames = 0;
            _collectionMaxFrameMs = 0;
            _collecting = true;
        }

        /// <summary>What a block of a frozen grid costs to build, learned as they are built.</summary>
        private static double _msPerBlock;

        private static void CollectSome()
        {
            var watch = Stopwatch.StartNew();
            var done = 0;
            while (_collectIndex < _toCollect.Count && watch.Elapsed.TotalMilliseconds < FrameBudgetMs)
            {
                var grid = _toCollect[_collectIndex];
                // the next grid only if it should fit in what is left of the budget (at least one a frame): the
                // last grid of a frame took it to 5-8 ms before every save
                if (done > 0 && watch.Elapsed.TotalMilliseconds + grid.BlocksCount * _msPerBlock > FrameBudgetMs) break;
                _collectIndex++;
                if (grid.MarkedForClose || grid.Closed || !FreezeLogic.FrozenGrids.Contains(grid.EntityId)) continue;
                var gridStarted = watch.Elapsed.TotalMilliseconds;
                var entry = new Entry { Grid = grid };
                entry.BlockChanged = _ => entry.Stale = true;
                entry.Closing = _ => entry.Stale = true;
                grid.OnBlockAdded += entry.BlockChanged;
                grid.OnBlockRemoved += entry.BlockChanged;
                grid.OnMarkForClose += entry.Closing;
                Entries[grid.EntityId] = entry;
                grid.BeforeSave();
                entry.Builder = grid.GetObjectBuilder();
                done++;
                var perBlock = (watch.Elapsed.TotalMilliseconds - gridStarted) / Math.Max(1, grid.BlocksCount);
                _msPerBlock = _msPerBlock <= 0 ? perBlock : _msPerBlock * 0.8 + perBlock * 0.2;
            }
            _collectionFrames++;
            _collectionMaxFrameMs = Math.Max(_collectionMaxFrameMs, watch.Elapsed.TotalMilliseconds);
        }

        /// <summary>
        /// Prepared builder for the grid, if it is still valid. Called from the snapshot (game thread).
        /// </summary>
        public static bool TryTake(MyCubeGrid grid, out MyObjectBuilder_EntityBase builder)
        {
            builder = null;
            if (!Entries.TryGetValue(grid.EntityId, out var entry)) return false;
            if (entry.Stale || entry.Grid != grid || !FreezeLogic.FrozenGrids.Contains(grid.EntityId))
            {
                LastSnapshotStaleGrids++;
                return false;
            }
            builder = entry.Builder;
            if (builder is MyObjectBuilder_CubeGrid gridBuilder)
            {
                gridBuilder.GridPresenceTier = grid.GridPresenceTier;
                gridBuilder.PlayerPresenceTier = grid.PlayerPresenceTier;
            }
            if (VerifyPreparedBuilders) Verify(grid, builder);
            LastSnapshotPreparedGrids++;
            return true;
        }

        private static void Verify(MyCubeGrid grid, MyObjectBuilder_EntityBase prepared)
        {
            var expected = Xml(grid.GetObjectBuilder());
            var actual = Xml(prepared);
            if (expected == actual) return;
            LastVerifyMismatches++;
            if (LastVerifyFirstDifference != null) return;
            var a = expected.Split((char)10);
            var b = actual.Split((char)10);
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i])
                {
                    LastVerifyFirstDifference = grid.DisplayName + " line " + i + ": fresh '" + a[i].Trim() + "' prepared '" + b[i].Trim() + "'";
                    return;
                }
            LastVerifyFirstDifference = grid.DisplayName + ": length " + a.Length + " vs " + b.Length;
        }

        private static string Xml(MyObjectBuilder_Base builder)
        {
            using (var stream = new System.IO.MemoryStream())
            {
                VRage.ObjectBuilders.Private.MyObjectBuilderSerializerKeen.SerializeXML(stream, builder);
                return System.Text.Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>Resets per-snapshot statistics; called at the start of MyEntities.Save.</summary>
        public static void BeginSnapshot()
        {
            LastSnapshotPreparedGrids = 0;
            LastSnapshotStaleGrids = 0;
            LastVerifyMismatches = 0;
            LastVerifyFirstDifference = null;
            LastSnapshotBuiltGridIds = new HashSet<long>();
        }

        public static void RecordBuiltInSnapshot(MyCubeGrid grid) => LastSnapshotBuiltGridIds.Add(grid.EntityId);

        /// <summary>Called at the end of MyEntities.Save: prepared builders are single-use.</summary>
        public static void EndSnapshot()
        {
            if (_collecting) return;
            _waitForSnapshotFrames = 0;
            Clear();
        }

        /// <summary>Called by the freezer whenever a grid is unfrozen (also for wake-ups).</summary>
        public static void Invalidate(long gridId)
        {
            MySandboxGame.Static?.Invoke(() =>
            {
                if (Entries.TryGetValue(gridId, out var entry)) entry.Stale = true;
            }, "FrozenGridSaveCache.Invalidate");
        }

        private static void InventoryChangedSuffix(MyInventoryBase __instance)
        {
            if (Entries.Count == 0) return;
            var block = __instance.Entity as MyCubeBlock;
            if (block?.CubeGrid != null && Entries.TryGetValue(block.CubeGrid.EntityId, out var entry))
                entry.Stale = true;
        }

        private static void Clear()
        {
            foreach (var entry in Entries.Values)
            {
                entry.Grid.OnBlockAdded -= entry.BlockChanged;
                entry.Grid.OnBlockRemoved -= entry.BlockChanged;
                entry.Grid.OnMarkForClose -= entry.Closing;
            }
            Entries.Clear();
        }
    }
}
