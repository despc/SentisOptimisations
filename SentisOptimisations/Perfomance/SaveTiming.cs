using System;
using System.Diagnostics;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// Where the snapshot frame of a world save goes, one line in the log per save: the checkpoint, the
    /// entities (the sector), the voxel data, the rest - and the garbage collections in it. A handful of
    /// timestamps a save.
    /// </summary>
    [PatchShim]
    public static class SaveTiming
    {
        private static long _checkpoint, _sector, _voxels, _started, _voxelsStart, _partStart;
        private static int _gc0, _gc1, _gc2, _voxelCalls;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("SaveTiming", ctx, c =>
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo Own(string name) => typeof(SaveTiming).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            void Around(MethodInfo target, string prefix, string suffix)
            {
                if (target == null) throw new MissingMethodException(prefix);
                c.GetPattern(target).Prefixes.Add(Own(prefix));
                c.GetPattern(target).Suffixes.Add(Own(suffix));
            }
            Around(typeof(MySession).GetMethod("GetCheckpoint", any), nameof(CheckpointStart), nameof(CheckpointEnd));
            Around(typeof(MySession).GetMethod("GetSector", any), nameof(PartStart), nameof(SectorEnd));
            Around(typeof(MyVoxelMaps).GetMethod("GetVoxelMapsData", any), nameof(VoxelsStart), nameof(VoxelsEnd));
            Around(typeof(MySession).GetMethod("SaveDataComponents", any, null, Type.EmptyTypes, null), nameof(PartStart), nameof(ComponentsEnd));
        });

        private static long Now => Stopwatch.GetTimestamp();
        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        // the checkpoint is the first part of the snapshot
        private static void CheckpointStart()
        {
            _started = _partStart = Now;
            _sector = _voxels = 0;
            _voxelCalls = 0;
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
        }

        private static void CheckpointEnd() { if (_started != 0) _checkpoint = Now - _partStart; }
        private static void PartStart() => _partStart = Now;
        private static void SectorEnd() { if (_started != 0) _sector = Now - _partStart; }
        private static void VoxelsStart() => _voxelsStart = Now;
        private static void VoxelsEnd() { if (_started != 0) { _voxels += Now - _voxelsStart; _voxelCalls++; } }

        // saving the data components is the last part
        private static void ComponentsEnd()
        {
            if (_started == 0) return;
            var total = Now - _started;
            _started = 0;
            SentisOptimisationsPlugin.Log.Info(
                $"Save snapshot: {Ms(total):0.0} ms - checkpoint {Ms(_checkpoint):0.0}, entities {Ms(_sector):0.0}, voxels {Ms(_voxels):0.0} ({_voxelCalls} passes), " +
                $"rest {Ms(total - _checkpoint - _sector - _voxels):0.0}; collections {GC.CollectionCount(0) - _gc0}/{GC.CollectionCount(1) - _gc1}/{GC.CollectionCount(2) - _gc2}");
        }
    }
}
