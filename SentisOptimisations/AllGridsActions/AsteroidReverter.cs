using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
        private Random r = new Random();
        private static HashSet<IMyVoxelMap> inQueue = new HashSet<IMyVoxelMap>();
        private static bool inProcess = false;

        public void CheckAndRestore(HashSet<IMyVoxelMap> myVoxelMaps)
        {
            if (inProcess)
            {
                return;
            }
            if (inQueue.Count > 0)
            {
                inProcess = true;
                DoRestoreSavedAsteroid(inQueue.First());
                return;
            }
            var configAsteroidsRestoreCooldown = SentisOptimisationsPlugin.Config.AsteroidsRestoreCooldown;
            if (cooldown < configAsteroidsRestoreCooldown)
            {
                cooldown++;
                return;
            }
            cooldown = 0;
            Log.Warn("Start check and revert asteroids");
            var configPathToAsters = SentisOptimisationsPlugin.Config.PathToAsters;
            foreach (var voxelMap in myVoxelMaps)
            {
                Thread.Sleep(10);
                if (voxelMap == null)
                {
                    continue;
                }
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
                byte[] bytes = File.ReadAllBytes(pathToAster);
                IMyStorage newStorage = MyAPIGateway.Session.VoxelMaps.CreateStorage(bytes) as IMyStorage;
                var lengthNew = newStorage.GetVoxelData().Length;
                var lengthCurrent = ((IMyStorage)voxelMap.Storage).GetVoxelData().Length;
                if (lengthCurrent == lengthNew)
                {
                    continue;
                }
                inQueue.Add(voxelMap);
                
            }
            Log.Warn(inQueue.Count + " asteroids to restore queue size");
        }

        private static bool IsEmptySpace(IMyVoxelMap voxelMap)
        {
            var pos = voxelMap.GetPosition();
            var range = SentisOptimisationsPlugin.Config.AsteroidsRestoreRange;
            BoundingSphereD sphere = new BoundingSphereD(pos, range);
            var entitiesInSphere = MyEntities.GetEntitiesInSphere(ref sphere);
            var myEntities = entitiesInSphere.Where(entity => (entity is MyCharacter || entity is MyCubeGrid)
                                                              && Vector3D.Distance(entity.PositionComp.GetPosition(), pos) <
                                                              range).ToHashSet();
            foreach (var myEntity in myEntities)
            {
                Log.Info("found entity - " + myEntity.DisplayName);
            }

            if (myEntities.Any())
            {
                Log.Info("" + voxelMap.StorageName + " revert cancelled");
                return false;
            }
            return true;
        }


        public static void DoRestoreSavedAsteroid(IMyVoxelMap voxelMap)
        {
            try
            {
                Log.Warn("start DoRestoreSavedAsteroid " + voxelMap.StorageName);
                
                var configPathToAsters = SentisOptimisationsPlugin.Config.PathToAsters;
                var voxelMapStorageName = voxelMap.StorageName;
                if (string.IsNullOrEmpty(voxelMapStorageName))
                {
                    inQueue.Remove(voxelMap);
                    inProcess = false;
                    return;
                }

                var asteroidName = voxelMapStorageName + ".vx2";
                var pathToAster = configPathToAsters + "\\" + asteroidName;
                if (!File.Exists(pathToAster))
                {
                    inQueue.Remove(voxelMap);
                    inProcess = false;
                    return;
                }
                
                Vector3D position = voxelMap.PositionComp.GetPosition();
                byte[] bytes = File.ReadAllBytes(pathToAster);
                IMyStorage newStorage = MyAPIGateway.Session.VoxelMaps.CreateStorage(bytes) as IMyStorage;
                

                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    try
                    {
                        if (!IsEmptySpace(voxelMap))
                        {
                            inQueue.Remove(voxelMap);
                            inProcess = false;
                            return;
                        }

                        Log.Warn("start " + voxelMap.StorageName + " revert");
                        voxelMap.Close();
                        var addVoxelMap =
                            MyWorldGenerator.AddVoxelMap(voxelMapStorageName, (MyStorageBase)newStorage, position);
                        addVoxelMap.PositionComp.SetPosition(position);
                        Log.Warn("" + voxelMap.StorageName + " reverted");
                    }
                    catch (Exception e)
                    {
                        Log.Error("Exception ", e);
                    }

                    inQueue.Remove(voxelMap);
                    inProcess = false;
                });
            }
            catch (Exception e)
            {
                inQueue.Remove(voxelMap);
                inProcess = false;
                Log.Error("Exception ", e);
            }
        }
    }
}