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
    /// A grid whose builder runs code that is not the game's own is built on the game thread, in order, as in
    /// vanilla: a programmable block's builder runs the player's script (<c>MyProgrammableBlock.UpdateStorage</c> calls
    /// the script's Save()), and a mod's game logic may override GetObjectBuilder. Neither is thread-safe. Scripts ran
    /// on sixteen workers at once, and a script whose Save() the plugin measured as too slow (a collection in the
    /// middle) was punished there - its block damaged, its Havok bodies closed and made again off the game thread. On
    /// the old server that corrupted native memory: a clr stub overwritten, a stack walk overrun, an access violation
    /// in Havok - seven crashes on 28-29.09.2026, every one of them seconds after a save.
    /// A frozen grid is still built on the workers (most of a big world is frozen, and the punishment that did the
    /// damage is never done off the game thread any more: PBFix) - unless a script on it has a Save() with code in it
    /// (ScriptSaveCode): what a script does there, it must not do off the game thread. The Save() of the template
    /// every script starts from is empty, and most scripts have that one or none.
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
                    FrozenGridSaveCache.RecordBuiltInSnapshot(grid);
                    // user or mod code in the builder: here, on the game thread. Of a frozen grid, only a script's
                    // Save() that does something
                    if (FreezeLogic.FrozenGrids.Contains(grid.EntityId) ? ScriptSaves(grid) : RunsForeignCode(grid)) builders[i] = grid.GetObjectBuilder();
                    else grids.Add(i);
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

        private static readonly MyObjectBuilderType ProgrammableBlockType = typeof(Sandbox.Common.ObjectBuilders.MyObjectBuilder_MyProgrammableBlock);
        private static readonly Dictionary<Type, bool> OverridesBuilder = new Dictionary<Type, bool>();
        private static FieldInfo _compositeLogics;

        /// <summary>
        /// Whether building the grid runs code that is not the game's: a programmable block (the script's Save()) or a
        /// mod game logic that overrides GetObjectBuilder. Game thread.
        /// </summary>
        public static bool RunsForeignCode(MyCubeGrid grid)
        {
            if (grid.BlocksCounters.TryGetValue(ProgrammableBlockType, out var pbs) && pbs > 0) return true;
            foreach (var block in grid.GetFatBlocks())
                if (block.GameLogic is VRage.Game.Components.MyGameLogicComponent logic && ModLogicBuilds(logic)) return true;
            return false;
        }

        /// <summary>Whether a programmable block of the grid has a script whose Save() has code in it. Game thread.</summary>
        public static bool ScriptSaves(MyCubeGrid grid)
        {
            if (!grid.BlocksCounters.TryGetValue(ProgrammableBlockType, out var pbs) || pbs <= 0) return false;
            foreach (var block in grid.GetFatBlocks())
                if (block is Sandbox.Game.Entities.Blocks.MyProgrammableBlock pb && SentisOptimisationsPlugin.PBFix.SaveHasCode(pb)) return true;
            return false;
        }

        private static bool ModLogicBuilds(VRage.Game.Components.MyGameLogicComponent logic)
        {
            if (logic is Sandbox.Game.Entities.MyCompositeGameLogicComponent composite)
            {
                _compositeLogics = _compositeLogics ?? typeof(Sandbox.Game.Entities.MyCompositeGameLogicComponent)
                    .GetField("m_logicComponents", BindingFlags.Instance | BindingFlags.NonPublic);
                if (_compositeLogics?.GetValue(composite) is System.Collections.IEnumerable parts)
                {
                    foreach (var part in parts)
                        if (part is VRage.Game.Components.MyGameLogicComponent inner && ModLogicBuilds(inner)) return true;
                    return false;
                }
                return true;
            }
            var type = logic.GetType();
            if (!OverridesBuilder.TryGetValue(type, out var overrides))
            {
                var method = type.GetMethod("GetObjectBuilder", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(bool) }, null);
                var declaring = method?.DeclaringType?.Assembly;
                // the game's own logic types live in the game's assemblies; a mod's are compiled at load
                overrides = method != null && declaring != typeof(VRage.Game.Components.MyGameLogicComponent).Assembly &&
                            declaring != typeof(MyCubeGrid).Assembly && declaring != typeof(SpaceEngineers.Game.Entities.Blocks.MyTimerBlock).Assembly;
                OverridesBuilder[type] = overrides;
            }
            return overrides;
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
