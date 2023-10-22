using System.Collections.Generic;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.WorldEnvironment;
using VRage.Game.Entity;
using VRage.Game.ModAPI;

namespace SentisGameplayImprovements.AllGridsActions
{
    public class EntitiesObserver
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static HashSet<MyEntity> EntitiesToShipTools = new HashSet<MyEntity>();
        public static HashSet<MySafeZone> Safezones = new HashSet<MySafeZone>();
        public static HashSet<MyCubeGrid> MyCubeGrids = new HashSet<MyCubeGrid>();
        public static HashSet<IMyVoxelMap> VoxelMaps = new HashSet<IMyVoxelMap>();
        public static HashSet<MyPlanet> Planets = new HashSet<MyPlanet>();

        public static void MyEntitiesOnOnEntityRemove(MyEntity entity)
        {
            if (entity is MyEnvironmentSector
                || entity is MyCubeGrid
                || entity is MyPlanet
                || entity is IMyVoxelMap
                || entity is MyCharacter)
            {
                EntitiesToShipTools.Remove(entity);
            }

            if (entity is MyCubeGrid)
            {
                MyCubeGrids.Remove((MyCubeGrid) entity);
                return;
            }

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
            if (entity is MyEnvironmentSector
                || entity is MyCubeGrid
                || entity is MyPlanet
                || entity is IMyVoxelMap
                || entity is MyCharacter)
            {
                EntitiesToShipTools.Add(entity);
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

            if (entity is MySafeZone)
            {
                Safezones.Add((MySafeZone) entity);
            }
        }
    }
}