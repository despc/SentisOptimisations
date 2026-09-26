using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using VRage.Voxels;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The compressed copy of a voxel storage is rebuilt in the background, so a connecting player
    /// does not stop the server while it is built for them.
    ///
    /// A client that arrives near a planet is sent the planet's storage as one compressed blob, and
    /// <c>MyVoxelReplicable.Serialize</c> asks <c>MyStorageBase.Save</c> for it <b>on the game
    /// thread</b>. The game does keep that blob - and throws it away on every single change of the
    /// storage, so on a planet that players dig, it is almost never there when it is needed. On a
    /// planet dug for a few months the blob is tens of megabytes, and building it takes seconds of
    /// frozen server: measured on the stand, 3.5 MB cost 604 ms and a 758 ms frame.
    ///
    /// Nothing is patched here. The storage of every voxel map reports its changes, and this waits
    /// for the digging to stop - <see cref="QuietSeconds"/> - and then calls the same
    /// <c>Save</c> from a worker thread, which fills the game's own cache under the game's own locks
    /// (the world save does the same from its task). The blob is then already there when a client
    /// asks for it, and the game thread pays nothing.
    ///
    /// On a planet that is dug without pause the quiet moment never comes, so a blob that has been
    /// missing for <see cref="MaxColdSeconds"/> is rebuilt anyway; if it is invalidated again right
    /// after, the next attempt waits <see cref="MinRebuildIntervalSeconds"/> so this cannot spin.
    /// </summary>
    public class VoxelStreamCache
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>How long the digging has to stop before the blob is rebuilt.</summary>
        private const double QuietSeconds = 8;

        /// <summary>A storage without a blob for this long is rebuilt even while it keeps changing.</summary>
        private const double MaxColdSeconds = 60;

        /// <summary>The same storage is not rebuilt more often than this.</summary>
        private const double MinRebuildIntervalSeconds = 30;

        /// <summary>Storages smaller than this are left to the game: building them costs nothing.</summary>
        private const int MinInterestingBytes = 256 * 1024;

        private const double LoopSeconds = 2;

        private sealed class Tracked
        {
            public MyVoxelBase Voxel;
            public Action<Vector3I, Vector3I, MyStorageDataTypeFlags> Handler;
            public DateTime LastChange;
            public DateTime ColdSince;
            public DateTime LastRebuild;
            public int LastBlobBytes;
        }

        private readonly ConcurrentDictionary<long, Tracked> _tracked = new ConcurrentDictionary<long, Tracked>();

        public CancellationTokenSource CancellationTokenSource { get; set; }

        /// <summary>Blobs rebuilt off the game thread since the world was loaded.</summary>
        public static long Rebuilt;

        /// <summary>Milliseconds of background work those rebuilds took.</summary>
        public static long RebuiltMs;

        public void OnLoaded()
        {
            CancellationTokenSource = new CancellationTokenSource();
            MyEntities.OnEntityAdd += OnEntityAdd;
            MyEntities.OnEntityRemove += OnEntityRemove;
            foreach (var voxel in MyEntities.GetEntities().OfType<MyVoxelBase>().ToList()) Track(voxel);
            Task.Run(Loop);
        }

        public void OnUnloading()
        {
            CancellationTokenSource?.Cancel();
            MyEntities.OnEntityAdd -= OnEntityAdd;
            MyEntities.OnEntityRemove -= OnEntityRemove;
            foreach (var id in _tracked.Keys.ToList()) Forget(id);
        }

        private void OnEntityAdd(VRage.Game.Entity.MyEntity entity)
        {
            if (entity is MyVoxelBase voxel) Track(voxel);
        }

        private void OnEntityRemove(VRage.Game.Entity.MyEntity entity)
        {
            if (entity is MyVoxelBase voxel) Forget(voxel.EntityId);
        }

        /// <summary>Follows one voxel map's storage: every change makes its blob cold.</summary>
        private void Track(MyVoxelBase voxel)
        {
            if (voxel?.Storage == null || voxel.EntityId == 0) return;
            if (voxel.RootVoxel != null && voxel.RootVoxel != voxel) return; // a physics sector of a planet
            if (_tracked.ContainsKey(voxel.EntityId)) return;

            var now = DateTime.UtcNow;
            var tracked = new Tracked { Voxel = voxel, LastChange = now, ColdSince = now, LastRebuild = DateTime.MinValue };
            if (!_tracked.TryAdd(voxel.EntityId, tracked)) return;

            tracked.Handler = (min, max, flags) =>
            {
                tracked.LastChange = DateTime.UtcNow;
                if (tracked.ColdSince == DateTime.MaxValue) tracked.ColdSince = tracked.LastChange;
            };
            voxel.Storage.RangeChanged += tracked.Handler;
        }

        private void Forget(long entityId)
        {
            if (!_tracked.TryRemove(entityId, out var tracked)) return;
            try
            {
                if (tracked.Voxel?.Storage != null && tracked.Handler != null)
                    tracked.Voxel.Storage.RangeChanged -= tracked.Handler;
            }
            catch (Exception e)
            {
                Log.Warn(e, "Could not unsubscribe from a voxel storage");
            }
        }

        private async void Loop()
        {
            var token = CancellationTokenSource.Token;
            try
            {
                Log.Info("Voxel stream cache started");
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(LoopSeconds), token);
                    foreach (var tracked in _tracked.Values.ToList())
                    {
                        if (token.IsCancellationRequested) return;
                        try
                        {
                            Rebuild(tracked);
                        }
                        catch (Exception e)
                        {
                            Log.Error(e, "Rebuilding a voxel blob failed");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The world is unloading.
            }
            catch (Exception e)
            {
                Log.Error(e, "Voxel stream cache loop stopped");
            }
        }

        private void Rebuild(Tracked tracked)
        {
            var voxel = tracked.Voxel;
            var storage = voxel?.Storage as MyStorageBase;
            if (storage == null || voxel.Closed || voxel.MarkedForClose || storage.Closed)
            {
                Forget(voxel?.EntityId ?? 0);
                return;
            }

            if (storage.AreDataCached)
            {
                tracked.ColdSince = DateTime.MaxValue;
                return;
            }

            var now = DateTime.UtcNow;
            if (tracked.ColdSince == DateTime.MaxValue) tracked.ColdSince = now;
            if ((now - tracked.LastRebuild).TotalSeconds < MinRebuildIntervalSeconds) return;

            var quiet = (now - tracked.LastChange).TotalSeconds >= QuietSeconds;
            var overdue = (now - tracked.ColdSince).TotalSeconds >= MaxColdSeconds;
            if (!quiet && !overdue) return;

            // Small storages are not worth a background pass; the game builds them in no time.
            if (tracked.LastBlobBytes != 0 && tracked.LastBlobBytes < MinInterestingBytes)
            {
                tracked.LastRebuild = now;
                return;
            }

            var startedAt = Stopwatch.GetTimestamp();
            var data = VoxelBlob.Get(storage);     // compressed outside the storage's lock
            var ms = (long)((Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency);

            tracked.LastRebuild = DateTime.UtcNow;
            tracked.ColdSince = DateTime.MaxValue;
            tracked.LastBlobBytes = data?.Length ?? 0;
            Interlocked.Increment(ref Rebuilt);
            Interlocked.Add(ref RebuiltMs, ms);

            if (SentisOptimisationsPlugin.Config.EnableMainDebugLogs)
            {
                Log.Info($"Voxel blob of {voxel.StorageName} rebuilt off the game thread: " +
                         $"{tracked.LastBlobBytes / 1024 / 1024.0:F1} MB in {ms} ms");
            }
        }

        /// <summary>What the background rebuilds have saved the game thread, for the statistics line.</summary>
        public static string Describe() =>
            Interlocked.Read(ref Rebuilt) == 0
                ? "no voxel blobs rebuilt yet"
                : $"{Interlocked.Read(ref Rebuilt)} voxel blobs rebuilt off the game thread, {Interlocked.Read(ref RebuiltMs)} ms of work";
    }
}
