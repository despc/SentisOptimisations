using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using NLog;
using Sandbox.Engine.Voxels;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The voxel data of a world save, written with the leaves of the octree on all cores.
    ///
    /// A save takes the data of every changed voxel map in the frame of its snapshot
    /// (<c>MyVoxelMaps.GetVoxelMapsData</c> -> <c>MyStorageBase.GetVoxelData</c> -> <c>MyOctreeStorage.SaveInternal</c>),
    /// and on a planet that is being dug the game's cached copy is never there: the whole storage is written out
    /// on the game thread, every save, and it grows with every pit. Measured on the stand with five bots digging:
    /// 54-171 ms of a 62-173 ms save frame (the entities took 2-5 ms).
    ///
    /// Almost all of that is the leaves: each is a small octree of its own, written one after the other. Here the
    /// head of the file (storage access, metadata, the material table, the macro nodes) is written by the game's
    /// own methods as before, and the leaves are written in batches on all cores, each batch into a buffer of its
    /// own, then put into the stream in the game's order: the same bytes, in the same frame, under the storage's
    /// lock the caller holds - the snapshot stays what it was. The first large storage written after the start is
    /// also written the vanilla way and the two compared byte for byte; a difference turns this off until restart.
    /// </summary>
    [PatchShim]
    public static class ParallelVoxelSave
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>Storages with fewer leaves than this are left to the game.</summary>
        private const int MinLeaves = 2048;

        /// <summary>Leaves per batch.</summary>
        private const int BatchLeaves = 512;

        private static bool _disabled, _verified;
        [ThreadStatic] private static bool _vanilla;

        private static MethodInfo _writeStorageAccess, _writeStorageMetaData, _writeMaterialTable, _writeDataProvider, _writeOctreeNodes, _saveAccess;
        private static FieldInfo _contentNodes, _materialNodes, _contentLeaves, _materialLeaves, _dataProvider;
        private static Type _saveAccessType;
        private static Action<object, Stream> _writeLeaf;
        private static Func<object, int> _chunkType, _chunkSize;

        /// <summary>Saves written here, and milliseconds they took (for the log).</summary>
        public static long Written, WrittenMs;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("ParallelVoxelSave", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var storage = typeof(MyOctreeStorage);
            MethodInfo M(Type t, string name) => t.GetMethod(name, any) ?? throw new MissingMethodException(t.Name, name);
            FieldInfo F(string name) => storage.GetField(name, any) ?? throw new MissingFieldException(storage.Name, name);
            _writeStorageAccess = M(typeof(MyStorageBase), "WriteStorageAccess");
            _saveAccess = M(typeof(MyStorageBase), "SaveAccess");
            _writeStorageMetaData = M(storage, "WriteStorageMetaData");
            _writeMaterialTable = M(storage, "WriteMaterialTable");
            _writeDataProvider = M(storage, "WriteDataProvider");
            _writeOctreeNodes = M(storage, "WriteOctreeNodes");
            _saveAccessType = _writeOctreeNodes.GetParameters()[3].ParameterType;
            _contentNodes = F("m_contentNodes");
            _materialNodes = F("m_materialNodes");
            _contentLeaves = F("m_contentLeaves");
            _materialLeaves = F("m_materialLeaves");
            _dataProvider = F("m_dataProvider");

            var leafType = storage.Assembly.GetType("Sandbox.Engine.Voxels.MyMicroOctreeLeaf", true);
            var leafInterface = storage.Assembly.GetType("Sandbox.Engine.Voxels.IMyOctreeLeafNode", true);
            Func<object, int> Getter(string name)
            {
                var property = leafInterface.GetProperty(name) ?? throw new MissingMemberException("IMyOctreeLeafNode." + name);
                var getter = new DynamicMethod("Get" + name, typeof(int), new[] { typeof(object) }, storage, true);
                var g = getter.GetILGenerator();
                g.Emit(OpCodes.Ldarg_0);
                g.Emit(OpCodes.Castclass, leafInterface);
                g.Emit(OpCodes.Callvirt, property.GetGetMethod(true));
                g.Emit(OpCodes.Ret);
                return (Func<object, int>)getter.CreateDelegate(typeof(Func<object, int>));
            }
            _chunkType = Getter("SerializedChunkType");
            _chunkSize = Getter("SerializedChunkSize");
            var writeTo = leafType.GetMethod("WriteTo", any, null, new[] { typeof(Stream) }, null) ?? throw new MissingMethodException("MyMicroOctreeLeaf.WriteTo");
            var method = new DynamicMethod("WriteLeaf", null, new[] { typeof(object), typeof(Stream) }, storage, true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, leafType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, writeTo);
            il.Emit(OpCodes.Ret);
            _writeLeaf = (Action<object, Stream>)method.CreateDelegate(typeof(Action<object, Stream>));

            ctx.GetPattern(storage.GetMethod("SaveInternal", any, null, new[] { typeof(Stream) }, null) ?? throw new MissingMethodException("MyOctreeStorage.SaveInternal"))
                .Prefixes.Add(typeof(ParallelVoxelSave).GetMethod(nameof(SaveInternalPrefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool SaveInternalPrefix(MyOctreeStorage __instance, Stream stream)
        {
            if (_disabled || _vanilla) return true;
            try
            {
                var content = (IDictionary)_contentLeaves.GetValue(__instance);
                var material = (IDictionary)_materialLeaves.GetValue(__instance);
                if (content.Count + material.Count < MinLeaves) return true;
                var started = Stopwatch.GetTimestamp();
                if (!_verified) return Verify(__instance, stream);
                // into a buffer of its own first: the stream gets the bytes only once they are all there, so a
                // failure leaves it untouched and the game writes the data itself
                lock (Whole)
                {
                    Whole.SetLength(0);
                    Write(__instance, Whole, content, material);
                    stream.Write(Whole.GetBuffer(), 0, (int)Whole.Length);
                }
                Written++;
                WrittenMs += (long)((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
                return false;
            }
            catch (Exception e)
            {
                _disabled = true;
                Log.Error(e, "Parallel voxel save failed; the game writes voxel data itself until restart");
                return true;
            }
        }

        /// <summary>The whole file before it goes to the caller's stream (kept between saves).</summary>
        private static readonly MemoryStream Whole = new MemoryStream(1 << 20);

        /// <summary>The first time: both ways, compared; the stream gets the vanilla bytes either way.</summary>
        private static bool Verify(MyOctreeStorage storage, Stream stream)
        {
            var ours = new MemoryStream();
            var watch = Stopwatch.StartNew();
            Write(storage, ours, (IDictionary)_contentLeaves.GetValue(storage), (IDictionary)_materialLeaves.GetValue(storage));
            var oursMs = watch.Elapsed.TotalMilliseconds;
            var vanilla = new MemoryStream();
            watch.Restart();
            _vanilla = true;
            try { InvokeVanilla(storage, vanilla); }
            finally { _vanilla = false; }
            var vanillaMs = watch.Elapsed.TotalMilliseconds;
            var a = ours.GetBuffer();
            var b = vanilla.GetBuffer();
            var same = ours.Length == vanilla.Length;
            for (long i = 0; same && i < ours.Length; i++) same = a[i] == b[i];
            _verified = true;
            if (same) Log.Info($"Parallel voxel save checked: {vanilla.Length / 1024} KB the same as the game's; {oursMs:0.0} ms against {vanillaMs:0.0} ms");
            else
            {
                _disabled = true;
                Log.Error($"Parallel voxel save differs from the game's ({ours.Length} against {vanilla.Length} bytes); off until restart");
            }
            stream.Write(b, 0, (int)vanilla.Length);
            return false;
        }

        private static void InvokeVanilla(MyOctreeStorage storage, Stream stream) =>
            typeof(MyOctreeStorage).GetMethod("SaveInternal", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Stream) }, null)
                .Invoke(storage, new object[] { stream });

        private static void Write(MyOctreeStorage storage, Stream stream, IDictionary content, IDictionary material)
        {
            _writeStorageAccess.Invoke(storage, new object[] { stream });
            _writeStorageMetaData.Invoke(storage, new object[] { stream });
            _writeMaterialTable.Invoke(null, new object[] { stream });
            _writeDataProvider.Invoke(null, new object[] { stream, _dataProvider.GetValue(storage) });
            var saveAccess = Delegate.CreateDelegate(_saveAccessType, storage, _saveAccess);
            _writeOctreeNodes.Invoke(null, new object[] { stream, MyOctreeStorage.ChunkTypeEnum.MacroContentNodes, _contentNodes.GetValue(storage), saveAccess });
            _writeOctreeNodes.Invoke(null, new object[] { stream, MyOctreeStorage.ChunkTypeEnum.MacroMaterialNodes, _materialNodes.GetValue(storage), null });
            WriteLeaves(stream, content);
            WriteLeaves(stream, material);
            new MyOctreeStorage.ChunkHeader { ChunkType = MyOctreeStorage.ChunkTypeEnum.EndOfFile }.WriteTo(stream);
        }

        // batch buffers, kept between saves (one save at a time writes them: the callers hold the storage lock,
        // and two storages saved at once would only share the pool under the lock below)
        private static readonly List<MemoryStream> Buffers = new List<MemoryStream>();
        private static readonly object BuffersLock = new object();

        /// <summary>What vanilla WriteOctreeLeaves writes, the leaves in batches on all cores.</summary>
        private static void WriteLeaves(Stream stream, IDictionary leaves)
        {
            var keys = new ulong[leaves.Count];
            var values = new object[leaves.Count];
            var n = 0;
            foreach (DictionaryEntry entry in leaves)
            {
                keys[n] = (ulong)entry.Key;
                values[n] = entry.Value;
                n++;
            }
            var batches = (n + BatchLeaves - 1) / BatchLeaves;
            lock (BuffersLock)
            {
                while (Buffers.Count < batches) Buffers.Add(new MemoryStream(64 * 1024));
                Parallel.For(0, batches, batch =>
                {
                    var buffer = Buffers[batch];
                    buffer.SetLength(0);
                    for (var i = batch * BatchLeaves; i < Math.Min(n, (batch + 1) * BatchLeaves); i++)
                        WriteLeaf(buffer, keys[i], values[i]);
                });
                for (var batch = 0; batch < batches; batch++)
                    stream.Write(Buffers[batch].GetBuffer(), 0, (int)Buffers[batch].Length);
            }
        }

        private static void WriteLeaf(Stream stream, ulong key, object leaf)
        {
            var type = (MyOctreeStorage.ChunkTypeEnum)_chunkType(leaf);
            new MyOctreeStorage.ChunkHeader { ChunkType = type, Size = _chunkSize(leaf) + 8, Version = 3 }.WriteTo(stream);
            stream.WriteNoAlloc(key);
            switch (type)
            {
                case MyOctreeStorage.ChunkTypeEnum.ContentLeafOctree:
                case MyOctreeStorage.ChunkTypeEnum.MaterialLeafOctree:
                    _writeLeaf(leaf, stream);
                    break;
                case MyOctreeStorage.ChunkTypeEnum.ContentLeafProvider:
                case MyOctreeStorage.ChunkTypeEnum.MaterialLeafProvider:
                    break;
                default:
                    throw new InvalidOperationException("unknown leaf chunk " + type);
            }
        }
    }
}
