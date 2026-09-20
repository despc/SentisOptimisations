using System;
using System.Collections.Generic;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.WorldEnvironment;
using SentisOptimisationsPlugin;
using SentisOptimisationsPlugin.AllGridsActions;
using SentisOptimisationsPlugin.Freezer;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using SentisOptimisations;
using SentisOptimisations.Utils;

namespace SentisGameplayImprovements.AllGridsActions
{
    public class EntitiesObserver
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static ConcurrentHashSet<MySafeZone> Safezones = new ConcurrentHashSet<MySafeZone>();
        public static ConcurrentHashSet<MyCubeGrid> MyCubeGrids = new ConcurrentHashSet<MyCubeGrid>();
        public static ConcurrentHashSet<IMyVoxelMap> VoxelMaps = new ConcurrentHashSet<IMyVoxelMap>();
        public static ConcurrentHashSet<MyPlanet> Planets = new ConcurrentHashSet<MyPlanet>();

        public static void MyEntitiesOnOnEntityRemove(MyEntity entity)
        {
            if (entity is MyCubeGrid)
            {
                MyCubeGrids.Remove((MyCubeGrid) entity);
                // drops frozen-state + compensation stamps: a stale stamp under a recycled
                // EntityId would hand the next block that gets this id a bogus delta
                FreezeLogic.ForgetGrid((MyCubeGrid) entity);
                return;
            }

            if (entity is Sandbox.Game.Entities.MyCubeBlock cubeBlock)
            {
                CompensationTracker.Forget(cubeBlock.EntityId);
            }

            GasTankOptimisations.CleanupEntity(entity);
            PBFix.CleanupEntity(entity);
            GridSystemUpdatePatch.CleanupEntity(entity);
            SafezonePatch.CleanupEntity(entity);

            if (entity is MyPlanet)
            {
                Planets.Remove((MyPlanet) entity);
                return;
            }

            if (entity is IMyVoxelMap)
            {
                VoxelMaps.Remove((IMyVoxelMap) entity);
                return;
            }

            if (entity is MySafeZone)
            {
                Safezones.Remove((MySafeZone) entity);
            }
        }

        public static void MyEntitiesOnOnEntityAdd(MyEntity entity)
        {
            if (entity is MySafeZone)
            {
                Safezones.Add((MySafeZone) entity);
            }

            if (entity is MyPlanet)
            {
                Log.Warn("Add planet to list " + entity.DisplayName);
                Planets.Add((MyPlanet) entity);
                return;
            }

            if (entity is MyCubeGrid)
            {
                MyCubeGrids.Add((MyCubeGrid) entity);
                return;
            }

            if (entity is IMyVoxelMap)
            {
                VoxelMaps.Add((IMyVoxelMap) entity);
                return;
            }
        }

        /// <summary>
        /// The OnEntityAdd event only fires for entities spawned after subscription; entities
        /// that were already registered when the session loaded must be collected explicitly,
        /// otherwise the AABB discovery cache starts empty for a pre-built world.
        /// </summary>
        public static void PrimeFromAllEntities()
        {
            try
            {
                var field = typeof(Sandbox.Game.Entities.MyEntities).EasyField("m_entities", false);
                var all = field?.GetValue(null) as System.Collections.IEnumerable;
                if (all == null)
                {
                    Log.Warn("EntitiesObserver priming skipped: m_entities not found");
                    return;
                }

                var added = 0;
                // MyConcurrentHashSet: safe to enumerate while the game mutates it
                foreach (var obj in all)
                {
                    if (obj is MyEntity entity)
                    {
                        MyEntitiesOnOnEntityAdd(entity);
                        added++;
                    }
                }

                Log.Info($"EntitiesObserver primed from world: {added} entities");
            }
            catch (Exception e)
            {
                Log.Error(e, "EntitiesObserver priming failed");
            }
        }

        public static void ClearAll()
        {
            Safezones.Clear();
            MyCubeGrids.Clear();
            VoxelMaps.Clear();
            Planets.Clear();
            CompensationTracker.ClearAll();
        }
    }
}