using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Sandbox.Game.Entities;
using SentisOptimisationsPlugin.Freezer;
using Torch.Managers.PatchManager;
using VRage.Collections;
using VRage.Game.Entity;
using VRage.ObjectBuilders;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Builds the object builders of cube grids in parallel during a world save.
    ///
    /// A save takes its snapshot on the game thread (MySession.Save -> GetSector -> MyEntities.Save),
    /// and almost all of that time is MyCubeGrid.GetObjectBuilder of every grid: ~120 ms for 32k
    /// blocks, one frozen frame per save, growing with the world. Here BeforeSave and the builders of
    /// non-grid entities still run sequentially on the game thread in the vanilla order; only the grid
    /// builders are produced on all cores while the game thread waits, so nothing in the world changes
    /// during the snapshot and the save stays atomic. Result order matches vanilla.
    ///
    /// Grid builders read their own grid's state; the SentisTests save_perf bench checks that parallel
    /// and sequential builders serialize to identical XML. If a parallel build ever throws, the
    /// snapshot is rebuilt sequentially and parallel saving is disabled until the server restarts.
    ///
    /// Measured (64 grids, 32k blocks): the snapshot frame went from 135-151 ms to ~77 ms. The rest is
    /// garbage collection triggered by the builders themselves (6-7 gen0 + 2 gen1 in that frame).
    /// GC.TryStartNoGCRegion around the build is not an option: it starts with a full blocking
    /// collection (measured 1 s on the first save).
    /// </summary>
    [PatchShim]
    public static class ParallelEntitySave
    {
        private static readonly FieldInfo EntitiesField =
            typeof(MyEntities).GetField("m_entities", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly FieldInfo EntitiesToDeleteField =
            typeof(MyEntities).GetField("m_entitiesToDelete", BindingFlags.Static | BindingFlags.NonPublic);

        private static bool _disabled;

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("ParallelEntitySave", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            if (EntitiesField == null || EntitiesToDeleteField == null)
                throw new MissingFieldException("MyEntities.m_entities / m_entitiesToDelete not found");
            var save = typeof(MyEntities).GetMethod("Save", BindingFlags.Static | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            ctx.GetPattern(save).Prefixes.Add(typeof(ParallelEntitySave).GetMethod(nameof(SavePrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool SavePrefix(ref List<MyObjectBuilder_EntityBase> __result)
        {
            if (_disabled) return true;
            List<MyEntity> entities;
            try
            {
                entities = EntitiesToSave();
            }
            catch (Exception e)
            {
                Disable(e);
                return true;
            }

            var builders = new MyObjectBuilder_EntityBase[entities.Count];
            var grids = new List<int>();
            FrozenGridSaveCache.BeginSnapshot();
            for (var i = 0; i < entities.Count; i++)
            {
                var grid = entities[i] as MyCubeGrid;
                // Frozen grids may already have been built over the previous frames (see FrozenGridSaveCache).
                if (grid != null && FrozenGridSaveCache.TryTake(grid, out var prepared))
                {
                    builders[i] = prepared;
                    continue;
                }
                entities[i].BeforeSave();
                if (grid != null)
                {
                    grids.Add(i);
                    FrozenGridSaveCache.RecordBuiltInSnapshot(grid);
                }
                else builders[i] = entities[i].GetObjectBuilder();
            }

            try
            {
                Parallel.For(0, grids.Count, k => builders[grids[k]] = entities[grids[k]].GetObjectBuilder());
            }
            catch (Exception e)
            {
                Disable(e);
                foreach (var index in grids)
                    builders[index] = entities[index].GetObjectBuilder();
            }

            FrozenGridSaveCache.EndSnapshot();
            __result = new List<MyObjectBuilder_EntityBase>(builders);
            return false;
        }

        /// <summary>Same selection as vanilla MyEntities.Save.</summary>
        public static List<MyEntity> EntitiesToSave()
        {
            var all = (MyConcurrentHashSet<MyEntity>)EntitiesField.GetValue(null);
            var toDelete = (HashSet<MyEntity>)EntitiesToDeleteField.GetValue(null);
            var result = new List<MyEntity>();
            foreach (var entity in all)
                if (entity.Save && !toDelete.Contains(entity) && !entity.MarkedForClose)
                    result.Add(entity);
            return result;
        }

        private static void Disable(Exception e)
        {
            _disabled = true;
            SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e,
                "Parallel world save failed; falling back to the vanilla sequential snapshot until restart");
        }
    }
}
