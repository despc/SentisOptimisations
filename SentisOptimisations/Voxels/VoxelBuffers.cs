using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Sandbox;
using Sandbox.Engine.Voxels;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRage.Game.Voxels;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The whole-storage writes of voxel data (the world save, the blob a connecting client is sent) done in buffers
    /// kept between them, so that only the result is new memory.
    ///
    /// A planet that is being dug is written out whole again and again: by the world save (raw, then gzipped by the
    /// save task) and by <see cref="VoxelStreamCache"/> (gzipped, for the clients). The game writes into a
    /// <c>MemoryStream</c> that starts at 16 KB and doubles its way up, then copies the result out: for a 16 MB
    /// storage some 50 MB of large arrays, garbage at once. Large arrays are collected only by a full collection, and
    /// that is what the server ran: 17 full collections in a quarter of an hour, one a minute, each a frame of up to
    /// 50 ms (the stand, bots digging).
    ///
    /// Here the storage is written into a pooled buffer, gzipped into another, and only the result - which the caller
    /// keeps - is allocated. The blob for the clients does not even get the raw copy (<see cref="CaptureRaw"/>).
    /// The bytes are the game's: the same header and the same <c>SaveInternal</c>.
    /// </summary>
    [PatchShim]
    public static class VoxelBuffers
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>Buffers kept for the next write; a few, as saves and blobs may run at once.</summary>
        private const int MaxPooled = 4;

        /// <summary>A buffer grown past this is not kept (a huge storage written once in a while).</summary>
        private const int MaxPooledBytes = 256 << 20;

        private static readonly ConcurrentBag<MemoryStream> Pool = new ConcurrentBag<MemoryStream>();
        private static Action<MyStorageBase, Stream> _saveInternal;
        private static PropertyInfo _storageNames;
        private static ConstructorInfo _cloudFileCtor;
        private static object[] _cloudFileDefaults;
        private static MethodInfo _getData;

        /// <summary>The first raw writes are also made the game's way and compared.</summary>
        private const int Verifications = 2;
        private static int _verified;

        /// <summary>Checked already for this build of the plugin and of the game (<see cref="VerifiedOnce"/>).</summary>
        private static bool? _checkedBefore;
        private static bool CheckedBefore() => _checkedBefore ?? (_checkedBefore = VerifiedOnce.Is("VoxelBuffers")).Value;
        private static volatile bool _disabled;
        [ThreadStatic] private static bool _vanilla;

        /// <summary>Set on a thread that wants the raw bytes of its next <c>GetVoxelData</c> in this buffer.</summary>
        [ThreadStatic] private static MemoryStream _capture;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("VoxelBuffers", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var getData = typeof(MyStorageBase).GetMethod("GetData", instance, null, new[] { typeof(bool) }, null)
                          ?? throw new MissingMethodException("MyStorageBase.GetData");
            var saveInternal = typeof(MyStorageBase).GetMethod("SaveInternal", instance, null, new[] { typeof(Stream) }, null)
                               ?? throw new MissingMethodException("MyStorageBase.SaveInternal");
            _saveInternal = (Action<MyStorageBase, Stream>)saveInternal.CreateDelegate(typeof(Action<MyStorageBase, Stream>));
            _getData = getData;
            var saveSnapshot = typeof(MySessionSnapshot).GetMethod("SaveVoxelSnapshot", instance)
                               ?? throw new MissingMethodException("MySessionSnapshot.SaveVoxelSnapshot");
            var parameters = saveSnapshot.GetParameters();
            if (parameters.Length != 5 || parameters[1].ParameterType != typeof(byte[]) || parameters[2].ParameterType != typeof(bool) ||
                parameters[3].ParameterType != typeof(ulong).MakeByRefType() || !typeof(System.Collections.IList).IsAssignableFrom(parameters[4].ParameterType))
                throw new InvalidOperationException("MySessionSnapshot.SaveVoxelSnapshot is not what VoxelBuffers knows");
            // the file entry the game makes with new MyCloudFile(path): its constructor takes the path first, the rest
            // left at their defaults
            var cloudFile = parameters[4].ParameterType.GetGenericArguments()[0];
            _cloudFileCtor = cloudFile.GetConstructors(instance)
                                 .FirstOrDefault(c => c.GetParameters().Length > 0 && c.GetParameters()[0].ParameterType == typeof(string) &&
                                                      c.GetParameters().Skip(1).All(p => p.HasDefaultValue))
                             ?? throw new MissingMethodException(cloudFile.FullName + "(string, ...)");
            _cloudFileDefaults = _cloudFileCtor.GetParameters().Skip(1).Select(p => p.DefaultValue).ToArray();
            _storageNames = typeof(MySessionSnapshot).GetProperty("VoxelStorageNameCache", instance)
                            ?? throw new MissingMemberException("MySessionSnapshot.VoxelStorageNameCache");
            MethodInfo Own(string name) => typeof(VoxelBuffers).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            ctx.GetPattern(getData).Prefixes.Add(Own(nameof(GetDataPrefix)));
            ctx.GetPattern(saveSnapshot).Prefixes.Add(Own(nameof(SaveVoxelSnapshotPrefix)));
        }

        public static MemoryStream Rent()
        {
            if (!Pool.TryTake(out var stream)) return new MemoryStream(1 << 20);
            stream.SetLength(0);
            return stream;
        }

        public static void Return(MemoryStream stream)
        {
            if (stream == null || stream.Capacity > MaxPooledBytes || Pool.Count >= MaxPooled) return;
            Pool.Add(stream);
        }

        /// <summary>
        /// <c>storage.GetVoxelData()</c> with the raw bytes written into <paramref name="into"/> instead of a new array
        /// (the call still takes the storage's lock, writes what is pending and lets the storage take a data cache).
        /// </summary>
        public static void CaptureRaw(MyStorageBase storage, MemoryStream into)
        {
            _capture = into;
            try { storage.GetVoxelData(); }
            finally { _capture = null; }
        }

        /// <summary>gzip of <paramref name="length"/> bytes of <paramref name="raw"/>, the result a new array.</summary>
        public static byte[] Gzip(byte[] raw, int length)
        {
            var packed = Rent();
            try
            {
                using (var gzip = new GZipStream(packed, CompressionMode.Compress, leaveOpen: true))
                    gzip.Write(raw, 0, length);
                return packed.ToArray();
            }
            finally { Return(packed); }
        }

        /// <summary>What <c>GetData</c> writes: the format's name and version, then the storage.</summary>
        private static void WriteRaw(MyStorageBase storage, MemoryStream stream)
        {
            stream.WriteNoAlloc("Octree");
            stream.Write7BitEncodedInt(2);
            _saveInternal(storage, stream);
        }

        private static bool GetDataPrefix(MyStorageBase __instance, bool compressed, ref byte[] __result)
        {
            if (_disabled || _vanilla || !(__instance is MyOctreeStorage)) return true;
            try
            {
                if (_verified < Verifications && !CheckedBefore())
                {
                    // not checked yet: the game thread writes the game's way (a check there would be a frame of both),
                    // the check is made off it - the blob for the clients, rebuilt in the background
                    if (MySandboxGame.Static?.UpdateThread == System.Threading.Thread.CurrentThread) return true;
                    if (!compressed) return Verify(__instance, ref __result);
                }
                if (!compressed && _capture != null)
                {
                    _capture.SetLength(0);
                    WriteRaw(__instance, _capture);
                    __result = Array.Empty<byte>();
                    return false;
                }
                var raw = Rent();
                try
                {
                    WriteRaw(__instance, raw);
                    __result = compressed ? Gzip(raw.GetBuffer(), (int)raw.Length) : raw.ToArray();
                }
                finally { Return(raw); }
                return false;
            }
            catch (Exception e)
            {
                _disabled = true;
                _capture?.SetLength(0);
                Log.Error(e, "Pooled voxel write failed; the game writes voxel data itself until restart");
                return true;
            }
        }

        /// <summary>The first raw writes (off the game thread): both ways, compared; the caller gets the game's bytes
        /// either way.</summary>
        private static bool Verify(MyStorageBase storage, ref byte[] result)
        {
            var ours = _capture ?? new MemoryStream();
            ours.SetLength(0);
            WriteRaw(storage, ours);
            byte[] vanilla;
            _vanilla = true;
            try { vanilla = (byte[])_getData.Invoke(storage, new object[] { false }); }
            finally { _vanilla = false; }
            var same = ours.Length == vanilla.Length;
            var buffer = ours.GetBuffer();
            for (var i = 0; same && i < vanilla.Length; i++) same = buffer[i] == vanilla[i];
            System.Threading.Interlocked.Increment(ref _verified);
            if (same)
            {
                Log.Info($"Pooled voxel write checked: {vanilla.Length / 1024} KB the same as the game's");
                if (_verified >= Verifications) VerifiedOnce.Mark("VoxelBuffers");
            }
            else
            {
                _disabled = true;
                Log.Error($"Pooled voxel write differs from the game's ({ours.Length} against {vanilla.Length} bytes); off until restart");
            }
            if (_capture != null)
            {
                if (!same)
                {
                    _capture.SetLength(0);
                    _capture.Write(vanilla, 0, vanilla.Length);
                }
                result = Array.Empty<byte>();
            }
            else result = vanilla;
            return false;
        }

        /// <summary>The save task's write of a raw voxel snapshot: gzipped in a pooled buffer, otherwise the game's.</summary>
        private static bool SaveVoxelSnapshotPrefix(MySessionSnapshot __instance, string storageName, byte[] snapshotData, bool compress, ref ulong size,
            System.Collections.IList fileList, ref bool __result)
        {
            if (!compress) return true;
            var path = Path.Combine(__instance.SavingDir, storageName + ".vx2");
            object entry;
            try
            {
                var arguments = new object[1 + _cloudFileDefaults.Length];
                arguments[0] = path;
                Array.Copy(_cloudFileDefaults, 0, arguments, 1, _cloudFileDefaults.Length);
                entry = _cloudFileCtor.Invoke(arguments);
            }
            catch (Exception e)
            {
                // before anything is written: the game's own method does it all
                Log.Error(e, "VoxelBuffers: no file entry for a voxel snapshot; the game writes it");
                return true;
            }
            fileList.Add(entry);
            try
            {
                var packed = Gzip(snapshotData, snapshotData.Length);
                File.WriteAllBytes(path, packed);
                size = (ulong)packed.Length;
                var names = (Dictionary<string, IMyStorage>)_storageNames.GetValue(__instance);
                if (names != null && names.TryGetValue(storageName, out var storage) && !storage.Closed) storage.SetDataCache(packed, true);
                __result = true;
            }
            catch (Exception e)
            {
                MySandboxGame.Log.WriteLine($"Failed to write voxel file '{path}'");
                MySandboxGame.Log.WriteLine(e);
                Log.Error(e, $"Failed to write voxel file '{path}'");
                size = 0;
                __result = false;
            }
            return false;
        }
    }
}
