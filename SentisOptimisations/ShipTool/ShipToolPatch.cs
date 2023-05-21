using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using NAPI;
using NLog;
using NLog.Fluent;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Weapons;
using Sandbox.Game.Weapons.Guns;
using Sandbox.Game.World;
using Sandbox.Game.WorldEnvironment;
using Sandbox.Game.WorldEnvironment.Modules;
using Sandbox.ModAPI;
using SentisOptimisations;
using SentisOptimisationsPlugin.AllGridsActions;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace SentisOptimisationsPlugin.ShipTool
{
    [PatchShim]
    public static class ShipToolPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static Dictionary<long, int> Cooldowns = new Dictionary<long, int>();
        public static Dictionary<long, int> NobodyToOff = new Dictionary<long, int>();
        public static readonly Random r = new Random();
        
        public static void Patch(PatchContext ctx)
        {

            var MethodActivateCommon = typeof(MyShipToolBase).GetMethod(
                "ActivateCommon", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodActivateCommon).Prefixes.Add(
                typeof(ShipToolPatch).GetMethod(nameof(ActivateCommonPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            ctx.GetPattern(typeof(MyCubeGrid).GetMethod(nameof(MyCubeGrid.GetBlocksInsideSpheres)))
                .Prefixes.Add(typeof(ShipToolPatch).GetMethod(nameof(GetBlocksInsideSpheresPatch),
                    BindingFlags.Static | BindingFlags.NonPublic));
            
            var DrillEnvironmentSector = typeof(MyDrillBase).GetMethod(
                "DrillEnvironmentSector", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(DrillEnvironmentSector).Prefixes.Add(
                typeof(ShipToolPatch).GetMethod(nameof(DrillEnvironmentSectorPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool DrillEnvironmentSectorPatch(MyDrillSensorBase.DetectionInfo entry,
            float speedMultiplier,
            out MyStringHash targetMaterial,
            MyDrillBase __instance,
            ref bool __result)
        {
            targetMaterial = MyStringHash.GetOrCompute("Wood");
            try
            {
                __instance.GetType().GetProperty("DrilledEntity").SetMethod.Invoke(__instance, new[] { entry.Entity });
                __instance.GetType().GetProperty("DrilledEntityPoint").SetMethod.Invoke(__instance, new object[] { entry.DetectionPoint });
                //__instance.DrilledEntity = entry.Entity;
                //__instance.DrilledEntityPoint = entry.DetectionPoint;
                if (Sync.IsServer)
                {
                    if ((int)__instance.easyGetField("m_lastItemId") != entry.ItemId)
                    {
                        __instance.easySetField("m_lastItemId", entry.ItemId);
                        __instance.easySetField("m_lastContactTime", MySandboxGame.TotalGamePlayTimeInMilliseconds);
                    }
                    if ((double) (MySandboxGame.TotalGamePlayTimeInMilliseconds - (int)__instance.easyGetField("m_lastContactTime")) > 1500.0 * (double) speedMultiplier)
                    {
                        MyBreakableEnvironmentProxy module = (entry.Entity as MyEnvironmentSector).GetModule<MyBreakableEnvironmentProxy>();
                        var drillEntity = ((MyEntity)__instance.easyGetField("m_drillEntity"));
                        Vector3D vector3D = drillEntity.WorldMatrix.Forward + drillEntity.WorldMatrix.Right;
                        vector3D.Normalize();
                        double num1 = 10.0;
                        float num2 = (float) (num1 * num1) * 100f;
                        int itemId = entry.ItemId;
                        Vector3D detectionPoint = entry.DetectionPoint;
                        Vector3D hitnormal = vector3D;
                        double impactEnergy = (double) num2;
                        module.BreakAt(itemId, detectionPoint, hitnormal, impactEnergy);
                        __instance.easySetField("m_lastContactTime", MySandboxGame.TotalGamePlayTimeInMilliseconds);
                        __instance.easySetField("m_lastItemId", 0);
                    }
                }
                __result = true;
                
            }
            catch (Exception e)
            {
                Log.Error("Exception during DrillEnvironmentSector", e);
            }

            return false;
        }

        private static void GetBlocksInsideSpheresPatch(MyCubeGrid __instance, ref BoundingSphereD sphere1,
            ref BoundingSphereD sphere2,
            ref BoundingSphereD sphere3,
            HashSet<MySlimBlock> blocks1,
            HashSet<MySlimBlock> blocks2,
            HashSet<MySlimBlock> blocks3,
            bool respectDeformationRatio,
            float detectionBlockHalfSize,
            ref MatrixD invWorldGrid)
        {
            try
            {
                blocks1.Clear();
                blocks2.Clear();
                blocks3.Clear();
                HashSet<MyCubeBlock> m_processedBlocks = new HashSet<MyCubeBlock>();
                Vector3D result;
                Vector3D.Transform(ref sphere3.Center, ref invWorldGrid, out result);
                Vector3I vector3I1 = Vector3I.Round((result - sphere3.Radius) * (double)__instance.GridSizeR);
                Vector3I vector3I2 = Vector3I.Round((result + sphere3.Radius) * (double)__instance.GridSizeR);
                Vector3 vector3 = new Vector3(detectionBlockHalfSize);
                BoundingSphereD boundingSphereD1 = new BoundingSphereD(result, sphere1.Radius);
                BoundingSphereD boundingSphereD2 = new BoundingSphereD(result, sphere2.Radius);
                BoundingSphereD boundingSphereD3 = new BoundingSphereD(result, sphere3.Radius);
                ConcurrentDictionary<Vector3I, MyCube> instanceMCubes =
                    (ConcurrentDictionary<Vector3I, MyCube>)__instance.easyGetField("m_cubes");
                if ((vector3I2.X - vector3I1.X) * (vector3I2.Y - vector3I1.Y) * (vector3I2.Z - vector3I1.Z) <
                    instanceMCubes.Count)
                {
                    Vector3I key = new Vector3I();
                    for (key.X = vector3I1.X; key.X <= vector3I2.X; ++key.X)
                    {
                        for (key.Y = vector3I1.Y; key.Y <= vector3I2.Y; ++key.Y)
                        {
                            for (key.Z = vector3I1.Z; key.Z <= vector3I2.Z; ++key.Z)
                            {
                                MyCube myCube;
                                if (instanceMCubes.TryGetValue(key, out myCube))
                                {
                                    MySlimBlock cubeBlock = myCube.CubeBlock;
                                    if (cubeBlock.FatBlock == null ||
                                        !m_processedBlocks.Contains(cubeBlock.FatBlock))
                                    {
                                        m_processedBlocks.Add(cubeBlock.FatBlock);
                                        if (respectDeformationRatio)
                                        {
                                            boundingSphereD1.Radius =
                                                sphere1.Radius * (double)cubeBlock.DeformationRatio;
                                            boundingSphereD2.Radius =
                                                sphere2.Radius * (double)cubeBlock.DeformationRatio;
                                            boundingSphereD3.Radius =
                                                sphere3.Radius * (double)cubeBlock.DeformationRatio;
                                        }

                                        BoundingBox boundingBox = cubeBlock.FatBlock == null
                                            ? new BoundingBox(cubeBlock.Position * __instance.GridSize - vector3,
                                                cubeBlock.Position * __instance.GridSize + vector3)
                                            : new BoundingBox(
                                                cubeBlock.Min * __instance.GridSize - __instance.GridSizeHalf,
                                                cubeBlock.Max * __instance.GridSize + __instance.GridSizeHalf);
                                        if (boundingBox.Intersects((BoundingSphere)boundingSphereD3))
                                        {
                                            if (boundingBox.Intersects((BoundingSphere)boundingSphereD2))
                                            {
                                                if (boundingBox.Intersects((BoundingSphere)boundingSphereD1))
                                                    blocks1.Add(cubeBlock);
                                                else
                                                    blocks2.Add(cubeBlock);
                                            }
                                            else
                                                blocks3.Add(cubeBlock);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    foreach (MyCube myCube in (IEnumerable<MyCube>)instanceMCubes.Values)
                    {
                        MySlimBlock cubeBlock = myCube.CubeBlock;
                        if (cubeBlock.FatBlock == null || !m_processedBlocks.Contains(cubeBlock.FatBlock))
                        {
                            m_processedBlocks.Add(cubeBlock.FatBlock);
                            if (respectDeformationRatio)
                            {
                                boundingSphereD1.Radius = sphere1.Radius * (double)cubeBlock.DeformationRatio;
                                boundingSphereD2.Radius = sphere2.Radius * (double)cubeBlock.DeformationRatio;
                                boundingSphereD3.Radius = sphere3.Radius * (double)cubeBlock.DeformationRatio;
                            }

                            BoundingBox boundingBox = cubeBlock.FatBlock == null
                                ? new BoundingBox(cubeBlock.Position * __instance.GridSize - vector3,
                                    cubeBlock.Position * __instance.GridSize + vector3)
                                : new BoundingBox(cubeBlock.Min * __instance.GridSize - __instance.GridSizeHalf,
                                    cubeBlock.Max * __instance.GridSize + __instance.GridSizeHalf);
                            if (boundingBox.Intersects((BoundingSphere)boundingSphereD3))
                            {
                                if (boundingBox.Intersects((BoundingSphere)boundingSphereD2))
                                {
                                    if (boundingBox.Intersects((BoundingSphere)boundingSphereD1))
                                        blocks1.Add(cubeBlock);
                                    else
                                        blocks2.Add(cubeBlock);
                                }
                                else
                                    blocks3.Add(cubeBlock);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("Exception during GetBlocksInsideSpheresPatch", e);
            }
        }

        private static bool ActivateCommonPatch(MyShipToolBase __instance)
        {
            try
            {
                var blockId = __instance.EntityId;

                if (SentisOptimisationsPlugin.Config.SlowdownEnabled &&
                    MySandboxGame.Static.SimulationFrameCounter > 6000)
                {
                    var myUpdateTiersPlayerPresence = __instance.CubeGrid.PlayerPresenceTier;
                    if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier1)
                    {
                        if (NeedSkip(blockId, 30)) return false;
                    }
                    else if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier2)
                    {
                        int nobodyToOffCount = 0;
                        if (NobodyToOff.TryGetValue(blockId, out nobodyToOffCount))
                        {
                            NobodyToOff[blockId] = nobodyToOffCount++;
                            if (nobodyToOffCount > 5000)
                            {
                                __instance.Enabled = false;
                                NobodyToOff.Remove(blockId);
                                return false;
                            }
                        }
                        else
                        {
                            NobodyToOff[blockId] = 0;
                        }

                        if (NeedSkip(blockId, 300)) return false;
                    }
                }

                DoActivateCommon(__instance);
            }
            catch (Exception e)
            {
                Log.Error("DoActivateCommon exception ", e);
            }

            return false;
        }

        private static void DoActivateCommon(MyShipToolBase __instance)
        {
            BoundingSphere m_detectorSphere =
                (BoundingSphere)__instance.easyGetField("m_detectorSphere", typeof(MyShipToolBase));
            BoundingSphereD boundingSphereD = new BoundingSphereD(
                Vector3D.Transform(m_detectorSphere.Center, __instance.CubeGrid.WorldMatrix),
                (double)m_detectorSphere.Radius);
            BoundingSphereD sphere = new BoundingSphereD(boundingSphereD.Center,
                (double)m_detectorSphere.Radius * 0.5);
            
            __instance.easySetField("m_isActivatedOnSomething", false, typeof(MyShipToolBase));
            bool flag = false;
            if (SentisOptimisationsPlugin.Config.AsyncWeld)
            {
                var shipToolsAsyncQueues = SentisOptimisationsPlugin.Instance.ShipToolsAsyncQueues;
                var runInFrame = MySession.Static.GameplayFrameCounter + r.Next(10, 60);
                if (!__instance.CubeGrid.IsStatic)
                {
                    runInFrame = -1;
                }
                shipToolsAsyncQueues.EnqueueAction(() =>
                {
                    List<MyEntity> topEntities = GetTopEntitiesInSphereAsync(boundingSphereD);
                    var entitiesInContactAsync = GetEntitiesInContact(__instance, topEntities, ref flag);
                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    {
                        ProcessEntitiesInContact(__instance, boundingSphereD, flag, entitiesInContactAsync, sphere);
                    }, StartAt: runInFrame);
                });
                return;
            }

            var topEntities = MyEntities.GetTopMostEntitiesInSphere(ref boundingSphereD);
            var entitiesInContactSync = GetEntitiesInContact(__instance, topEntities, ref flag);
            ProcessEntitiesInContact(__instance,  boundingSphereD, flag, entitiesInContactSync, sphere);
            topEntities.Clear();
        }

        private static void ProcessEntitiesInContact(MyShipToolBase __instance,
            BoundingSphereD boundingSphereD, bool flag, HashSet<MyEntity> entitiesInContact, BoundingSphereD sphere)
        {
            
            CheckEnvironment(__instance, boundingSphereD, flag);

            HashSet<MySlimBlock> blocksToActivateOnSync = new HashSet<MySlimBlock>();
            foreach (MyEntity myEntity in entitiesInContact)
            {
                MyCharacter myCharacter = myEntity as MyCharacter;
                MyCubeGrid myCubeGrid = myEntity as MyCubeGrid;

                if (myCubeGrid != null && !SentisOptimisationsPlugin.Config.AsyncWeld)
                {
                    HashSet<MySlimBlock> mTempBlocksBuffer = new HashSet<MySlimBlock>();
                    myCubeGrid.GetBlocksInsideSphere(ref boundingSphereD, mTempBlocksBuffer);
                    blocksToActivateOnSync.UnionWith(mTempBlocksBuffer);
                }

                if (myCharacter != null && Sync.IsServer)
                {
                    MyStringHash damageType = MyDamageType.Drill;
                    switch (__instance)
                    {
                        case IMyShipGrinder _:
                            damageType = MyDamageType.Grind;
                            break;
                        case IMyShipWelder _:
                            damageType = MyDamageType.Weld;
                            break;
                    }

                    if (new MyOrientedBoundingBoxD((BoundingBoxD)myCharacter.PositionComp.LocalAABB,
                            myCharacter.PositionComp.WorldMatrixRef).Intersects(ref sphere))
                        myCharacter.DoDamage(20f, damageType, true, __instance.EntityId);
                }
            }

            if (SentisOptimisationsPlugin.Config.AsyncWeld)
            {
                CollectTargetBlocksAsyncAndCallActivate(__instance, boundingSphereD, entitiesInContact);
                return;
            }
            CallActivate(__instance, blocksToActivateOnSync);
        }

        private static HashSet<MyEntity> GetEntitiesInContact(MyShipToolBase __instance, List<MyEntity> topEntities, ref bool flag)
        {
            HashSet<MyEntity> entitiesInContact = new HashSet<MyEntity>();
            try
            {
                foreach (MyEntity myEntity in topEntities)
                {
                    if (myEntity is MyEnvironmentSector)
                        flag = true;
                    MyEntity topMostParent = myEntity.GetTopMostParent((Type)null);
                    if ((bool)__instance.easyCallMethod("CanInteractWith", new object[] { topMostParent }, true,
                            typeof(MyShipToolBase)))
                        entitiesInContact.Add(topMostParent);
                }
            }
            catch (Exception e)
            {
                Log.Error("Async exception " + e);
            }
            return entitiesInContact;
        }

        private static List<MyEntity> GetTopEntitiesInSphereAsync(BoundingSphereD boundingSphereD)
        {
            try
            {
                List<MyEntity> entitiesInSphereAsync = new List<MyEntity>();
                foreach (var entity in new HashSet<MyEntity>(AllGridsObserver.entitiesToShipTools))
                {
                    if (entity.PositionComp.WorldAABB.Intersects(boundingSphereD))
                    {
                        entitiesInSphereAsync.Add(entity);
                    }
                }
                return entitiesInSphereAsync;
            }
            catch (Exception e)
            {
                Log.Error("Async exception " + e);
            }

            return new List<MyEntity>();
        }

        private static async void CollectTargetBlocksAsyncAndCallActivate(MyShipToolBase myShipToolBase,
            BoundingSphereD boundingSphereD, HashSet<MyEntity> entitiesInContact)
        {
            var shipToolsAsyncQueues = SentisOptimisationsPlugin.Instance.ShipToolsAsyncQueues;
            var asynActionsCount = shipToolsAsyncQueues.AsynActions.Count;
            var runInFrame = MySession.Static.GameplayFrameCounter + asynActionsCount + 1;
            shipToolsAsyncQueues.EnqueueAction(() =>
            {
                var resultBlocksToActivateOnAsync = CollectBlocks(new HashSet<MyEntity>(entitiesInContact));
                MyAPIGateway.Utilities.InvokeOnGameThread(() => CallActivate(myShipToolBase, resultBlocksToActivateOnAsync));
            });
            
            HashSet<MySlimBlock> CollectBlocks(HashSet<MyEntity> entitiesInContactAsync)
            {
                var blocksToActivateOnAsync = new HashSet<MySlimBlock>();
                try
                {
                    foreach (MyEntity myEntity in entitiesInContactAsync)
                    {
                        if (myEntity is MyCubeGrid myCubeGrid)
                        {
                            HashSet<MySlimBlock> mTempBlocksBuffer = new HashSet<MySlimBlock>();
                            myCubeGrid.GetBlocksInsideSphere(ref boundingSphereD, mTempBlocksBuffer);
                            blocksToActivateOnAsync.UnionWith(mTempBlocksBuffer);
                        }
                    }

                    return blocksToActivateOnAsync;
                }
                catch (Exception e)
                {
                    SentisOptimisationsPlugin.Log.Error(e);
                }

                return blocksToActivateOnAsync;
            }
        }

        private static void CheckEnvironment(MyShipToolBase __instance, BoundingSphereD boundingSphereD, bool flag)
        {
            var mCheckEnvironmentSector =
                (bool)__instance.easyGetField("m_checkEnvironmentSector", typeof(MyShipToolBase));
            
            if (!(mCheckEnvironmentSector & flag)) return;
            
            MyPhysics.HitInfo? nullable = MyPhysics.CastRay(boundingSphereD.Center,
                boundingSphereD.Center + boundingSphereD.Radius * __instance.WorldMatrix.Forward, 24);
            if (nullable.HasValue && nullable.HasValue)
            {
                IMyEntity hitEntity = nullable.Value.HkHitInfo.GetHitEntity();
                if (hitEntity is MyEnvironmentSector)
                {
                    MyEnvironmentSector environmentSector = hitEntity as MyEnvironmentSector;
                    uint shapeKey = nullable.Value.HkHitInfo.GetShapeKey(0);
                    int itemFromShapeKey = environmentSector.GetItemFromShapeKey(shapeKey);
                    if (environmentSector.DataView.Items[itemFromShapeKey].ModelIndex >= (short)0)
                    {
                        MyBreakableEnvironmentProxy module =
                            environmentSector.GetModule<MyBreakableEnvironmentProxy>();
                        Vector3D vector3D = __instance.CubeGrid.WorldMatrix.Right +
                                            __instance.CubeGrid.WorldMatrix.Forward;
                        vector3D.Normalize();
                        double num1 = 10.0;
                        float num2 = (float)(num1 * num1) * __instance.CubeGrid.Physics.Mass;
                        int itemId = itemFromShapeKey;
                        Vector3D position = (Vector3D)nullable.Value.HkHitInfo.Position;
                        Vector3D hitnormal = vector3D;
                        double impactEnergy = (double)num2;
                        module.BreakAt(itemId, position, hitnormal, impactEnergy);
                    }
                }
            }
        }

        private static void CallActivate(MyShipToolBase __instance, HashSet<MySlimBlock> m_blocksToActivateOn)
        {
            bool m_isActivatedOnSomething =
                (bool)__instance.easyGetField("m_isActivatedOnSomething", typeof(MyShipToolBase));

            var instanceMIsActivatedOnSomething = m_isActivatedOnSomething |
                                                  (bool)__instance.easyCallMethod("Activate",
                                                      new object[] { m_blocksToActivateOn });
            __instance.easySetField("m_isActivatedOnSomething", instanceMIsActivatedOnSomething,
                typeof(MyShipToolBase));

            int m_activateCounter = (int)__instance.easyGetField("m_activateCounter", typeof(MyShipToolBase));
            ReflectionUtils.SetInstanceField(typeof(MyShipToolBase), __instance, "m_activateCounter",
                m_activateCounter + 1);
            ReflectionUtils.SetInstanceField(typeof(MyShipToolBase), __instance, "m_lastTimeActivate",
                MySandboxGame.TotalGamePlayTimeInMilliseconds);
            ((HashSet<MySlimBlock>)__instance.easyGetField("m_blocksToActivateOn", typeof(MyShipToolBase))).Clear();
        }


        public static float GetWelderRadius(MyShipWelder welder)
        {
            return ((MyShipWelderDefinition)(welder.BlockDefinition)).SensorRadius;
        }

        private static void SetRadius(MyShipToolBase __instance, float radius)
        {
            BoundingSphere m_detectorSphere =
                (BoundingSphere)ReflectionUtils.GetInstanceField(typeof(MyShipToolBase), __instance,
                    "m_detectorSphere");
            BoundingSphere bs = new BoundingSphere(m_detectorSphere.Center, radius);
            ReflectionUtils.SetInstanceField(typeof(MyShipToolBase), __instance, "m_detectorSphere", bs);
        }
        
        private static bool NeedSkip(long blockId, int cd)
        {
            int cooldown;
            if (Cooldowns.TryGetValue(blockId, out cooldown))
            {
                if (cooldown > cd)
                {
                    Cooldowns[blockId] = 0;
                    return false;
                }
                Cooldowns[blockId] = cooldown + 1;
                return true;
            }

            Cooldowns[blockId] = r.Next(0, cd);
            return true;
        }
    }
}