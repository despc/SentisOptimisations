using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using NLog;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Library.Collections;
using VRage.Network;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Serialization;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The voxel storage a connecting client is sent is compressed on a worker thread instead of on
    /// the game thread.
    ///
    /// When a client comes near a planet, <c>MyVoxelReplicable.Serialize</c> turns the planet's whole
    /// storage into one compressed blob. Vanilla builds the blob inline - <c>Storage.Save</c> - and
    /// only then starts a parallel task that writes it into the stream. The game thread therefore
    /// stops for as long as the compression takes: measured on the stand, 604 ms for a blob of
    /// 3.5 MB, and a planet players have dug for months is tens of megabytes, which is the several
    /// seconds of freeze people see when somebody joins.
    ///
    /// The streaming protocol already expects this to take time: while the data is being made, the
    /// state group answers <c>Processing</c> and the replication server simply asks again later. So
    /// the fix is to build the blob on a worker thread, together with the writing that vanilla
    /// already does off the game thread. The bytes, the order of the writes and the data the client
    /// receives are exactly the same; only the thread changes. The object builder still comes from the game thread, because that one is not safe to
    /// read from anywhere else.
    ///
    /// <see cref="VoxelStreamCache"/> complements this: it keeps the blob built, so most of the time
    /// the worker finds it ready and the send costs nothing at all.
    /// </summary>
    [PatchShim]
    public static class VoxelStreamAsync
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>Blobs compressed on a worker thread instead of the game thread.</summary>
        public static long Offloaded;

        /// <summary>Milliseconds of compression kept off the game thread.</summary>
        public static long OffloadedMs;

        /// <summary>Blobs that were still built on the game thread, because the task ran inline.</summary>
        public static long OnGameThread;

        private static Type _replicableType;
        private static Func<object, MyVoxelBase> _voxel;
        private static MethodInfo _startSerializing;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("VoxelStreamAsync", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            _replicableType = typeof(MyVoxelBase).Assembly.GetType("Sandbox.Game.Replication.MyVoxelReplicable");
            if (_replicableType == null) throw new TypeLoadException("Sandbox.Game.Replication.MyVoxelReplicable");

            var serialize = _replicableType.GetMethod("Serialize",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(BitStream), typeof(HashSet<string>), typeof(Endpoint), typeof(Action) }, null);
            if (serialize == null) throw new MissingMethodException("MyVoxelReplicable.Serialize");

            _startSerializing = typeof(MyReplicationLayer).GetMethod("StartSerializingReplicable",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (_startSerializing == null) throw new MissingMethodException("MyReplicationLayer.StartSerializingReplicable");

            _voxel = BuildVoxelGetter();

            ctx.GetPattern(serialize).Prefixes.Add(
                typeof(VoxelStreamAsync).GetMethod(nameof(SerializePrefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>The replicable's entity, through the Instance property of its base class.</summary>
        private static Func<object, MyVoxelBase> BuildVoxelGetter()
        {
            var property = _replicableType.GetProperty("Instance",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
            if (property == null) throw new MissingMemberException("MyVoxelReplicable.Instance");
            var getter = property.GetMethod;
            return replicable => (MyVoxelBase)getter.Invoke(replicable, null);
        }

        private static bool SerializePrefix(object __instance, BitStream stream, HashSet<string> cachedData,
            Endpoint forClient, Action writeData)
        {
            MyVoxelBase voxel;
            bool isUserCreated, isFromPrefab, contentChanged, sendContent;
            string asteroidName;
            long entityId;
            MyObjectBuilder_EntityBase builder = null;

            try
            {
                voxel = _voxel(__instance);
                if (voxel == null || voxel.Closed || voxel.Storage == null) return true;

                // Exactly vanilla's decisions, taken on the game thread where they are read.
                isUserCreated = voxel.CreatedByUser && voxel.AsteroidName != null;
                isFromPrefab = voxel.Save;
                contentChanged = voxel.ContentChanged || voxel.BeforeContentChanged;
                sendContent = (cachedData == null || !cachedData.Contains(voxel.StorageName)) &&
                              (contentChanged || (isFromPrefab && !isUserCreated));
                sendContent |= voxel.AsteroidName == null;
                asteroidName = voxel.AsteroidName;
                entityId = voxel.EntityId;

                if (isFromPrefab)
                {
                    using ((IDisposable)_startSerializing.Invoke(null, new object[] { __instance, forClient }))
                    {
                        builder = voxel.GetObjectBuilder();
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Voxel stream prefix could not prepare; the game thread builds the blob as before");
                return true;
            }

            var storage = voxel.Storage;
            // A plain worker, not ParallelTasks: the game drains its own task pool inside the frame,
            // so a long job put there is waited for and the freeze simply moves.
            System.Threading.Tasks.Task.Run(delegate
            {
                try
                {
                    byte[] data = null;
                    if (sendContent)
                    {
                        // The part vanilla does on the game thread. MyStorageBase.Save takes the
                        // storage's own locks, which is how the world save calls it from its task.
                        var startedAt = Stopwatch.GetTimestamp();
                        var wasCached = storage.AreDataCached;
                        var onGameThread = Sandbox.MySandboxGame.Static?.UpdateThread == Thread.CurrentThread;
                        storage.Save(out data);
                        if (!wasCached && onGameThread) Interlocked.Increment(ref OnGameThread);
                        if (!wasCached)
                        {
                            Interlocked.Increment(ref Offloaded);
                            Interlocked.Add(ref OffloadedMs,
                                (long)((Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency));
                        }
                    }

                    MySerializer.Write(stream, ref isUserCreated);
                    MySerializer.Write(stream, ref isFromPrefab);
                    MySerializer.Write(stream, ref sendContent);
                    MySerializer.Write(stream, ref contentChanged);
                    if (sendContent)
                    {
                        MySerializer.Write(stream, ref data);
                    }
                    else if (isUserCreated)
                    {
                        MySerializer.Write(stream, ref asteroidName);
                        var material = PredefinedMaterial(storage);
                        MySerializer.Write(stream, ref material);
                    }

                    if (isFromPrefab)
                    {
                        MySerializer.Write(stream, ref builder, MyObjectBuilderSerializerKeen.Dynamic);
                    }
                    else
                    {
                        MySerializer.Write(stream, ref entityId);
                    }

                    writeData();
                }
                catch (Exception e)
                {
                    // The stream stays unfinished; the replication server drops this attempt and the
                    // client asks again rather than hanging on half a planet.
                    Log.Error(e, "Voxel stream send failed for " + forClient);
                }
            });

            cachedData?.Add(voxel.StorageName);
            return false;
        }

        /// <summary>
        /// The material of a predefined shape provider. The provider type is internal to the game,
        /// so it is asked by name; this only matters for user-created asteroids.
        /// </summary>
        private static string PredefinedMaterial(VRage.Game.Voxels.IMyStorage storage)
        {
            var provider = storage.DataProvider;
            if (provider == null || provider.GetType().Name != "MyPredefinedDataProvider") return "";
            var method = provider.GetType().GetMethod("GetVoxelMaterial",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return method?.Invoke(provider, null) as string ?? "";
        }

        /// <summary>What was kept off the game thread, for the statistics line.</summary>
        public static string Describe() =>
            Interlocked.Read(ref Offloaded) == 0
                ? "no voxel blobs sent yet"
                : $"{Interlocked.Read(ref Offloaded)} voxel blobs compressed off the game thread, " +
                  $"{Interlocked.Read(ref OffloadedMs)} ms kept out of frames";
    }
}
