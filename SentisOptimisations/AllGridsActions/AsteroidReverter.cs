using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.Game.Voxels;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    public class AsteroidReverter
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private int cooldown = 0;
        public void CheckAndRestore()
        {
            var configAsteroidsRestoreCooldown = SentisOptimisationsPlugin.Config.AsteroidsRestoreCooldown;
            if (cooldown < configAsteroidsRestoreCooldown)
            {
                cooldown++;
                return;
            }
            cooldown = 0;
            var configPathToAsters = SentisOptimisationsPlugin.Config.PathToAsters;
            foreach (var voxelMap in MyEntities.GetEntities().OfType<IMyVoxelMap>())
            {
                var voxelMapStorageName = voxelMap.StorageName;
                if (string.IsNullOrEmpty(voxelMapStorageName))
                {
                    continue;
                }

                var asteroidName = voxelMapStorageName + ".vx2";
                var pathToAster = configPathToAsters + "\\" + asteroidName;
                if (!File.Exists(pathToAster))
                {
                    continue;
                }

                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    var pos = voxelMap.GetPosition();
                    BoundingSphereD sphere = new BoundingSphereD(pos, SentisOptimisationsPlugin.Config.AsteroidsRestoreRange);
                    var entitiesInSphere = MyEntities.GetEntitiesInSphere(ref sphere);
                    var myEntities = entitiesInSphere.Where(entity => entity is MyCharacter || entity is MyCubeGrid)
                        .ToHashSet();
                    foreach (var myEntity in myEntities)
                    {
                        Log.Warn("Asteroid revert: found entity - " + myEntity.DisplayName);
                    }

                    if (myEntities.Any())
                    {
                        Log.Warn("Asteroid revert: " + voxelMap.StorageName + " revert cancelled");
                        return;
                    }

                    Task.Run(() => { DoRestoreSavedAsteroid(voxelMap); });
                });
            }
        }


        public static void DoRestoreSavedAsteroid(IMyVoxelMap voxelMap)
        {
            try
            {
                var configPathToAsters = SentisOptimisationsPlugin.Config.PathToAsters;
                var voxelMapStorageName = voxelMap.StorageName;
                if (string.IsNullOrEmpty(voxelMapStorageName))
                {
                    return;
                }

                var asteroidName = voxelMapStorageName + ".vx2";
                var pathToAster = configPathToAsters + "\\" + asteroidName;
                if (!File.Exists(pathToAster))
                {
                    return;
                }
                
                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    try
                    {
                        Vector3D position = voxelMap.PositionComp.GetPosition();
                        byte[] bytes = File.ReadAllBytes(pathToAster);
                        voxelMap.Close();
                        IMyStorage newStorage = MyAPIGateway.Session.VoxelMaps.CreateStorage(bytes) as IMyStorage;
                        var addVoxelMap =
                            MyWorldGenerator.AddVoxelMap(voxelMapStorageName, (MyStorageBase)newStorage, position);
                        addVoxelMap.PositionComp.SetPosition(position);
                        Log.Warn("Asteroid revert: " + voxelMap.StorageName + " reverted");
                    }
                    catch (Exception e)
                    {
                        Log.Error("Exception ", e);
                    }
                });
            }
            catch (Exception e)
            {
                Log.Error("Exception ", e);
            }
        }
    }
}