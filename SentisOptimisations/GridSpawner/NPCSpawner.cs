using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Havok;
using NLog;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GUI;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    public class NPCSpawner
    {
        static Random _random = new Random();
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        
        public static Vector3D? SpawnPosition(Vector3D targetPos)
        {
            Random random = new Random();
            MyEntity ignoreEnt = (MyEntity) null;
            BoundingSphereD boundingSphere = new BoundingSphereD(targetPos, (double) SentisOptimisationsPlugin.Config.GuardDistanceSpawn);
            foreach (MyEntity myEntity in MyEntities.GetEntitiesInSphere(ref boundingSphere))
            {
                if (myEntity is MySafeZone)
                    ignoreEnt = myEntity;
            }
            return MyEntities.FindFreePlaceCustom(boundingSphere.RandomToUniformPointOnSphere(random.NextDouble(), random.NextDouble()), SentisOptimisationsPlugin.Config.GuardDistanceSpawn, ignoreEnt: ignoreEnt);
        }
        

        public static void DoSpawnGrids(long masterIdentityId, string str, Vector3D spawnPosition,
            Vector3? up = null, Vector3? forward = null,
            SpawnDelegate.AddListenerDelegate addListenerDelegate = null, MyPlanet planet = null)
        {
            try
            {
                MyObjectBuilder_Definitions loadedPrefab = MyBlueprintUtils.LoadPrefab(str);
                MyObjectBuilder_CubeGrid[] cubeGrids = loadedPrefab.ShipBlueprints[0].CubeGrids;
                SpawnSomeGrids(cubeGrids, spawnPosition, masterIdentityId, up,
                    forward, addListenerDelegate);
            }
            catch (Exception e)
            {
                Log.Error(e, "Spawn NPC exception");
            }
        }


        public static void SpawnSomeGrids(MyObjectBuilder_CubeGrid[] cubeGrids,
            Vector3D position, long masterIdentityId, 
            Vector3? up = null, Vector3? forward = null, SpawnDelegate.AddListenerDelegate addListenerDelegate = null)
        {
            MyAPIGateway.Entities.RemapObjectBuilderCollection(cubeGrids);
            RemapOwnership(cubeGrids, masterIdentityId);
            Vector3D vector3D = cubeGrids[0].PositionAndOrientation.GetValueOrDefault().Position +
                                Vector3D.Zero;

            
            for (int index = 0; index < cubeGrids.Length; ++index)
            {
                MyObjectBuilder_CubeGrid cubeGrid = cubeGrids[index];

                if (index == 0)
                {
                    if (cubeGrid.PositionAndOrientation.HasValue)
                    {
                        MyPositionAndOrientation valueOrDefault = cubeGrid.PositionAndOrientation.GetValueOrDefault();
                        valueOrDefault.Position = position;
                        if (up.HasValue)
                        {
                            if (forward.HasValue)
                            {
                                valueOrDefault.Up = up.Value;
                                valueOrDefault.Forward = forward.Value;
                            }
                            else
                            {
                                valueOrDefault.Up = up.Value;
                                valueOrDefault.Forward = Vector3.CalculatePerpendicularVector(valueOrDefault.Up);
                            }
                        }
                        else if (forward.HasValue)
                        {
                            valueOrDefault.Forward = forward.Value;
                            valueOrDefault.Up = Vector3.CalculatePerpendicularVector(valueOrDefault.Forward);
                        }

                        cubeGrid.PositionAndOrientation = new MyPositionAndOrientation?(valueOrDefault);
                        
                    }
                    var myCubeBlocks = cubeGrid.CubeBlocks.Where(block => block is MyObjectBuilder_Cockpit &&  ((MyObjectBuilder_Cockpit)block).CustomName != null && ((MyObjectBuilder_Cockpit)block).CustomName.Contains("PosCock")).ToList();
                    if (myCubeBlocks.Count > 0)
                    {
                        var cockPos = myCubeBlocks[0];
                        var cockPosBlockOrientation = cockPos.BlockOrientation;
                        var cockFw = cockPosBlockOrientation.Forward;
                        MyPositionAndOrientation posNew = cubeGrid.PositionAndOrientation.GetValueOrDefault();
                        switch (cockFw)
                        {
                            case Base6Directions.Direction.Down:
                            {
                                posNew.Forward = posNew.Orientation.Up;
                                break;
                            }
                            case Base6Directions.Direction.Backward:
                            {
                                posNew.Forward = posNew.Orientation.Forward;
                                break;
                            }
                            case Base6Directions.Direction.Right:
                            {
                                posNew.Forward = -posNew.Orientation.Right;
                                break;
                            }
                            case Base6Directions.Direction.Left:
                            {
                                posNew.Forward = posNew.Orientation.Right;
                                break;
                            }
                            case Base6Directions.Direction.Up:
                            {
                                posNew.Forward = -posNew.Orientation.Up;
                                break;
                            }
                        }
                        var cockUp = cockPosBlockOrientation.Up;
                        switch (cockUp)
                        {
                            case Base6Directions.Direction.Down:
                            {
                                posNew.Up = posNew.Orientation.Up;
                                break;
                            }
                            case Base6Directions.Direction.Backward:
                            {
                                posNew.Up = posNew.Orientation.Forward;
                                break;
                            }
                            case Base6Directions.Direction.Right:
                            {
                                posNew.Up = -posNew.Orientation.Right;
                                break;
                            }
                            case Base6Directions.Direction.Left:
                            {
                                posNew.Up = posNew.Orientation.Right;
                                break;
                            }
                            case Base6Directions.Direction.Forward:
                            {
                                posNew.Up = -posNew.Orientation.Forward;
                                break;
                            }
                        }
                        cubeGrid.PositionAndOrientation = posNew;
                    }
                }
                else
                {
                    MyPositionAndOrientation valueOrDefault = cubeGrid.PositionAndOrientation.GetValueOrDefault();
                    valueOrDefault.Position = valueOrDefault.Position - vector3D;
                    cubeGrid.PositionAndOrientation = valueOrDefault;
                }
            }

            //TODO: Добавить проверку на коллизии
            for (int index = 0; index < cubeGrids.Length; ++index)
            {
                MyAPIGateway.Entities.CreateFromObjectBuilderParallel(cubeGrids[index],
                    completionCallback: new Action<IMyEntity>(
                        entity =>
                        {
                            ((MyCubeGrid) entity).DetectDisconnectsAfterFrame();
                            MyAPIGateway.Entities.AddEntity(entity);
                            if (addListenerDelegate != null)
                            {
                                addListenerDelegate.Invoke(((MyCubeGrid) entity));
                            }

                        }));
            }
        }


        public static void RemapOwnership(
            MyObjectBuilder_CubeGrid[] cubeGrids,
            long new_owner)
        {
            foreach (MyObjectBuilder_CubeGrid cubeGrid in cubeGrids)
            {
                foreach (MyObjectBuilder_CubeBlock cubeBlock in cubeGrid.CubeBlocks)
                {
                    cubeBlock.BuiltBy = new_owner;
                    cubeBlock.Owner = new_owner;
                    cubeBlock.ShareMode = MyOwnershipShareModeEnum.Faction;
                }
            }
        }

    }
}