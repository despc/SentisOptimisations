using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NAPI;
using NLog;
using ParallelTasks;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Utils;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Inventory;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.Game.WorldEnvironment;
using Sandbox.Game.WorldEnvironment.Modules;
using Sandbox.ModAPI;
using SentisOptimisations;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace SentisOptimisationsPlugin.ShipTool
{
    [PatchShim]
    public static class ShipToolPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static Random r = new Random();

        public static void Patch(PatchContext ctx)
        {
            // var MethodLoadDummies = typeof(MyShipToolBase).GetMethod(
            //     "LoadDummies", BindingFlags.Instance | BindingFlags.NonPublic);
            //
            // ctx.GetPattern(MethodLoadDummies).Suffixes.Add(
            //     typeof(ShipToolPatch).GetMethod(nameof(LoadDummiesPatch),
            //         BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodMyShipDrillInit = typeof(MyShipDrill).GetMethod(
                nameof(MyShipDrill.Init), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            ctx.GetPattern(MethodMyShipDrillInit).Suffixes.Add(
                typeof(ShipToolPatch).GetMethod(nameof(MyShipDrillInitPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodMyInventoryAddItems = typeof(MyInventory).GetMethods(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(info => info.Name.Equals("AddItems")).ToList()[0];
            
            ctx.GetPattern(MethodMyInventoryAddItems).Prefixes.Add(
                typeof(ShipToolPatch).GetMethod(nameof(MyInventoryAddItemsPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodAddItemsInternal = typeof(MyInventory).GetMethods(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(info => info.Name.Equals("AddItemsInternal")).ToList()[0];
            
            ctx.GetPattern(MethodAddItemsInternal).Prefixes.Add(
                typeof(ShipToolPatch).GetMethod(nameof(MyInventoryMethodAddItemsInternalPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
                
            var MethodActivateCommon = typeof(MyShipToolBase).GetMethod(
                "ActivateCommon", BindingFlags.Instance | BindingFlags.NonPublic);
            
            ctx.GetPattern(MethodActivateCommon).Prefixes.Add(
                typeof(ShipToolPatch).GetMethod(nameof(ActivateCommonPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            ctx.GetPattern(typeof(MyCubeGrid).GetMethod(nameof(MyCubeGrid.GetBlocksInsideSpheres)))
                .Prefixes.Add(typeof(ShipToolPatch).GetMethod(nameof(GetBlocksInsideSpheresPatch),
                    BindingFlags.Static | BindingFlags.NonPublic));
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
                Vector3I vector3I1 = Vector3I.Round((result - sphere3.Radius) * (double) __instance.GridSizeR);
                Vector3I vector3I2 = Vector3I.Round((result + sphere3.Radius) * (double) __instance.GridSizeR);
                Vector3 vector3 = new Vector3(detectionBlockHalfSize);
                BoundingSphereD boundingSphereD1 = new BoundingSphereD(result, sphere1.Radius);
                BoundingSphereD boundingSphereD2 = new BoundingSphereD(result, sphere2.Radius);
                BoundingSphereD boundingSphereD3 = new BoundingSphereD(result, sphere3.Radius);
                ConcurrentDictionary<Vector3I, MyCube> instanceMCubes = (ConcurrentDictionary<Vector3I, MyCube>) __instance.easyGetField("m_cubes");
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
                                                sphere1.Radius * (double) cubeBlock.DeformationRatio;
                                            boundingSphereD2.Radius =
                                                sphere2.Radius * (double) cubeBlock.DeformationRatio;
                                            boundingSphereD3.Radius =
                                                sphere3.Radius * (double) cubeBlock.DeformationRatio;
                                        }

                                        BoundingBox boundingBox = cubeBlock.FatBlock == null
                                            ? new BoundingBox(cubeBlock.Position * __instance.GridSize - vector3,
                                                cubeBlock.Position * __instance.GridSize + vector3)
                                            : new BoundingBox(cubeBlock.Min * __instance.GridSize - __instance.GridSizeHalf,
                                                cubeBlock.Max * __instance.GridSize + __instance.GridSizeHalf);
                                        if (boundingBox.Intersects((BoundingSphere) boundingSphereD3))
                                        {
                                            if (boundingBox.Intersects((BoundingSphere) boundingSphereD2))
                                            {
                                                if (boundingBox.Intersects((BoundingSphere) boundingSphereD1))
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
                    foreach (MyCube myCube in (IEnumerable<MyCube>) instanceMCubes.Values)
                    {
                        MySlimBlock cubeBlock = myCube.CubeBlock;
                        if (cubeBlock.FatBlock == null || !m_processedBlocks.Contains(cubeBlock.FatBlock))
                        {
                            m_processedBlocks.Add(cubeBlock.FatBlock);
                            if (respectDeformationRatio)
                            {
                                boundingSphereD1.Radius = sphere1.Radius * (double) cubeBlock.DeformationRatio;
                                boundingSphereD2.Radius = sphere2.Radius * (double) cubeBlock.DeformationRatio;
                                boundingSphereD3.Radius = sphere3.Radius * (double) cubeBlock.DeformationRatio;
                            }

                            BoundingBox boundingBox = cubeBlock.FatBlock == null
                                ? new BoundingBox(cubeBlock.Position * __instance.GridSize - vector3,
                                    cubeBlock.Position * __instance.GridSize + vector3)
                                : new BoundingBox(cubeBlock.Min * __instance.GridSize - __instance.GridSizeHalf,
                                    cubeBlock.Max * __instance.GridSize + __instance.GridSizeHalf);
                            if (boundingBox.Intersects((BoundingSphere) boundingSphereD3))
                            {
                                if (boundingBox.Intersects((BoundingSphere) boundingSphereD2))
                                {
                                    if (boundingBox.Intersects((BoundingSphere) boundingSphereD1))
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
        
        
        // private static void LoadDummiesPatch(MyShipToolBase __instance)
        // {
        //     try
        //     {
        //         BoundingSphere boundingSphere =
        //             (BoundingSphere) ReflectionUtils.GetInstanceField(typeof(MyShipToolBase), __instance,
        //                 "m_detectorSphere");
        //         boundingSphere.Radius = boundingSphere.Radius *
        //                                 SentisOptimisationsPlugin.Config.ShipGrinderWelderRadiusMultiplier;
        //         ReflectionUtils.SetInstanceField(typeof(MyShipToolBase), __instance, "m_detectorSphere",
        //             boundingSphere);
        //     }
        //     catch (Exception e)
        //     {
        //         Log.Error("Exception during LoadDummiesPatch", e);
        //     }
        // }

        private static bool ActivateCommonPatch(MyShipToolBase __instance)
        {
            if (__instance is MyShipWelder)
            {
                SetRadius(__instance, GetWelderRadius((MyShipWelder) __instance));
            }
            DoActivateCommon(__instance);
            return false;
        }

        private static void DoActivateCommon(MyShipToolBase __instance)
        {
            BoundingSphere m_detectorSphere = (BoundingSphere) __instance.easyGetField("m_detectorSphere", typeof(MyShipToolBase));
            BoundingSphereD boundingSphereD = new BoundingSphereD(
                Vector3D.Transform(m_detectorSphere.Center, __instance.CubeGrid.WorldMatrix),
                (double) m_detectorSphere.Radius);
            BoundingSphereD sphere = new BoundingSphereD(boundingSphereD.Center,
                (double) m_detectorSphere.Radius * 0.5);
            __instance.easySetField("m_isActivatedOnSomething", false, typeof(MyShipToolBase));
            List<MyEntity> entitiesInSphere = MyEntities.GetTopMostEntitiesInSphere(ref boundingSphereD);
            ActivateInGameThread(__instance, entitiesInSphere, boundingSphereD, sphere);
        }

        private static void ActivateInGameThread(MyShipToolBase __instance, List<MyEntity> entitiesInSphere,
            BoundingSphereD boundingSphereD, BoundingSphereD sphere)
        {
            bool flag = false;
            var m_entitiesInContact =
                ((HashSet<MyEntity>) __instance.easyGetField("m_entitiesInContact", typeof(MyShipToolBase)));
            m_entitiesInContact.Clear();
            foreach (MyEntity myEntity in entitiesInSphere)
            {
                if (myEntity is MyEnvironmentSector)
                    flag = true;
                MyEntity topMostParent = myEntity.GetTopMostParent((Type) null);
                if ((bool) __instance.easyCallMethod("CanInteractWith", new object[] {topMostParent}, true,
                    typeof(MyShipToolBase)))
                    m_entitiesInContact.Add(topMostParent);
            }

            bool m_checkEnvironmentSector = (bool) __instance.easyGetField("m_checkEnvironmentSector", typeof(MyShipToolBase));
            if (m_checkEnvironmentSector & flag)
            {
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
                        if (environmentSector.DataView.Items[itemFromShapeKey].ModelIndex >= (short) 0)
                        {
                            MyBreakableEnvironmentProxy module =
                                environmentSector.GetModule<MyBreakableEnvironmentProxy>();
                            Vector3D vector3D = __instance.CubeGrid.WorldMatrix.Right +
                                                __instance.CubeGrid.WorldMatrix.Forward;
                            vector3D.Normalize();
                            double num1 = 10.0;
                            float num2 = (float) (num1 * num1) * __instance.CubeGrid.Physics.Mass;
                            int itemId = itemFromShapeKey;
                            Vector3D position = (Vector3D) nullable.Value.HkHitInfo.Position;
                            Vector3D hitnormal = vector3D;
                            double impactEnergy = (double) num2;
                            module.BreakAt(itemId, position, hitnormal, impactEnergy);
                        }
                    }
                }
            }

            entitiesInSphere.Clear();
            HashSet<MySlimBlock> m_blocksToActivateOn2 =
                (HashSet<MySlimBlock>) __instance.easyGetField("m_blocksToActivateOn", typeof(MyShipToolBase));
            foreach (MyEntity myEntity in m_entitiesInContact)
            {
                MyCharacter myCharacter = myEntity as MyCharacter;

                MyCubeGrid myCubeGrid = myEntity as MyCubeGrid;
  
                if (myCubeGrid != null && !SentisOptimisationsPlugin.Config.AsyncWeld)
                {
                    HashSet<MySlimBlock> m_tempBlocksBuffer = new HashSet<MySlimBlock>();
                    myCubeGrid.GetBlocksInsideSphere(ref boundingSphereD, m_tempBlocksBuffer, true);
                            
                    m_blocksToActivateOn2.UnionWith((IEnumerable<MySlimBlock>) m_tempBlocksBuffer);
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

                    if (new MyOrientedBoundingBoxD((BoundingBoxD) myCharacter.PositionComp.LocalAABB,
                        myCharacter.PositionComp.WorldMatrixRef).Intersects(ref sphere))
                        myCharacter.DoDamage(20f, damageType, true, __instance.EntityId);
                }
            }

            if (SentisOptimisationsPlugin.Config.AsyncWeld)
            {
                Parallel.StartBackground(() => Action(m_entitiesInContact));
                void Action(HashSet<MyEntity> m_entitiesInContact2)
                {
                    try
                    {
                        var blocksToActivateOn = new HashSet<MySlimBlock>();
                        foreach (MyEntity myEntity in m_entitiesInContact2)
                        {
                            MyCubeGrid myCubeGrid = myEntity as MyCubeGrid;
                            if (myCubeGrid != null)
                            {
                                HashSet<MySlimBlock> m_tempBlocksBuffer = new HashSet<MySlimBlock>();
                                myCubeGrid.GetBlocksInsideSphere(ref boundingSphereD, m_tempBlocksBuffer, true);
                            
                                blocksToActivateOn.UnionWith((IEnumerable<MySlimBlock>) m_tempBlocksBuffer);
                            }
                        }
                        MyAPIGateway.Utilities.InvokeOnGameThread(() => CallActivate(__instance, blocksToActivateOn));
                    }
                    catch (Exception e)
                    {
                        SentisOptimisationsPlugin.Log.Error(e);
                    }
                }
                return;
            }
            CallActivate(__instance, m_blocksToActivateOn2);
        }

        private static void CallActivate(MyShipToolBase __instance, HashSet<MySlimBlock> m_blocksToActivateOn)
        {
            bool m_isActivatedOnSomething = (bool) __instance.easyGetField("m_isActivatedOnSomething", typeof(MyShipToolBase));

            var instanceMIsActivatedOnSomething = m_isActivatedOnSomething |
                                                  (bool) __instance.easyCallMethod("Activate",
                                                      new object[] {m_blocksToActivateOn});
            __instance.easySetField("m_isActivatedOnSomething", instanceMIsActivatedOnSomething, typeof(MyShipToolBase));

            int m_activateCounter = (int) __instance.easyGetField("m_activateCounter", typeof(MyShipToolBase));
            ReflectionUtils.SetInstanceField(typeof(MyShipToolBase), __instance, "m_activateCounter",
                m_activateCounter + 1);
            ReflectionUtils.SetInstanceField(typeof(MyShipToolBase), __instance, "m_lastTimeActivate",
                MySandboxGame.TotalGamePlayTimeInMilliseconds);
            ((HashSet<MySlimBlock>) __instance.easyGetField("m_blocksToActivateOn", typeof(MyShipToolBase))).Clear();
        }


        public static float GetWelderRadius(MyShipWelder welder)
        {
            if (!IsSuperWelder(welder)) return ((MyShipWelderDefinition) (welder.BlockDefinition)).SensorRadius;

            return SentisOptimisationsPlugin.Config.ShipSuperWelderRadius;
        }

        public static bool IsSuperWelder(MyShipWelder welder)
        {
            if (!welder.CubeGrid.IsStatic)
            {
                {
                    return false;
                }
            }

            var ownerId = welder.OwnerId;
            
            if (!welder.CustomData.Contains("[AZ_REWARD]"))
            {
                {
                    return false;
                }
            }
            
            foreach (var donation in SentisOptimisationsPlugin.Donations)
            {
                
                var steamId = MySession.Static.Players.TryGetSteamId(ownerId);
                if (donation.SteamId == (long) steamId && donation.Type == Donation.DonationType.WELDER)
                {
                    return true;
                }
            }
            
            var playerFaction = MySession.Static.Factions.GetPlayerFaction(ownerId);
            if (playerFaction == null)
            {
                {
                    return false;
                }
            }
            var factionTag = playerFaction.Tag;
            
            if (!SentisOptimisationsPlugin.Config.AzWinners.Contains(factionTag))
            {
                {
                    return false;
                }
            }

            return true;
        }

        private static void SetRadius(MyShipToolBase __instance, float radius)
        {
            BoundingSphere m_detectorSphere =
                (BoundingSphere) ReflectionUtils.GetInstanceField(typeof(MyShipToolBase), __instance,
                    "m_detectorSphere");
            BoundingSphere bs = new BoundingSphere(m_detectorSphere.Center, radius);
            ReflectionUtils.SetInstanceField(typeof(MyShipToolBase), __instance, "m_detectorSphere", bs);
        }

        private static void MyShipDrillInitPatch(MyShipDrill __instance)
        {
            try
            {
                MyInventoryBase inventoryBase = __instance.Components.Get<MyInventoryBase>();
                ((MyInventory) inventoryBase).FixInventoryVolume(500000);
                Log.Warn("DrillInit Set Inventory");
            }
            catch (Exception e)
            {
                Log.Error("Exception in during MyShipDrillInitPatch", e);
            }
        }
        
        private static bool MyInventoryAddItemsPatch(MyInventory __instance, ref bool __result, MyFixedPoint amount,
            MyObjectBuilder_Base objectBuilder,
            uint? itemId,
            int index = -1)
        {
            try
            {
                if (!(__instance.Entity is MyShipDrill))
                {
                    return true;
                }
                
                if (amount == (MyFixedPoint) 0)
                    __result = false;
                MyObjectBuilder_PhysicalObject objectBuilder1 = objectBuilder as MyObjectBuilder_PhysicalObject;
                MyDefinitionId id = objectBuilder.GetId();
                if (MyFakes.ENABLE_COMPONENT_BLOCKS)
                {
                    if (objectBuilder1 == null)
                    {
                        objectBuilder1 = (MyObjectBuilder_PhysicalObject) new MyObjectBuilder_BlockItem();
                        (objectBuilder1 as MyObjectBuilder_BlockItem).BlockDefId = (SerializableDefinitionId) id;
                    }
                    else
                    {
                        MyCubeBlockDefinition componentBlockDefinition = MyDefinitionManager.Static.TryGetComponentBlockDefinition(id);
                        if (componentBlockDefinition != null)
                        {
                            objectBuilder1 = (MyObjectBuilder_PhysicalObject) new MyObjectBuilder_BlockItem();
                            (objectBuilder1 as MyObjectBuilder_BlockItem).BlockDefId = (SerializableDefinitionId) componentBlockDefinition.Id;
                        }
                    }
                }
                if (objectBuilder1 == null || __instance.ComputeAmountThatFits(objectBuilder1.GetObjectId(), 0.0f, 0.0f) < amount)
                    __result =  false;
                if (Sandbox.Game.Multiplayer.Sync.IsServer)
                {
                    if (__instance.IsConstrained)
                        __instance.easyCallMethod("AffectAddBySurvival", new Object[]{amount, objectBuilder1});
                    if (amount == (MyFixedPoint) 0)
                        __result =  false;
                    __instance.easyCallMethod("AddItemsInternal", new Object[]{amount, objectBuilder1, itemId, index});
                }
                __result =  true;
            }
            catch (Exception e)
            {
                Log.Error("Exception in during MyShipDrillShootPatch", e);
            }

            return false;
        }
        
        private static bool MyInventoryMethodAddItemsInternalPatch(MyInventory __instance, MyFixedPoint amount,
            MyObjectBuilder_PhysicalObject objectBuilder,
            uint? itemId = null,
            int index = -1)
        {
            try
            {
                if (!(__instance.Entity is MyShipDrill))
                {
                    return true;
                }
                
                __instance.OnBeforeContentsChanged();
                MyFixedPoint maxValue = MyFixedPoint.MaxValue;
                MyInventoryItemAdapter inventoryItemAdapter = MyInventoryItemAdapter.Static;
                inventoryItemAdapter.Adapt(objectBuilder.GetObjectId());
                MyFixedPoint maxStack = inventoryItemAdapter.MaxStackAmount;
                if (!objectBuilder.CanStack(objectBuilder))
                    maxStack = (MyFixedPoint) 1;
                if (MyFakes.ENABLE_DURABILITY_COMPONENT)
                    __instance.easyCallMethod("FixDurabilityForInventoryItem", new Object[]{objectBuilder});
                bool flag = false;
                if (index >= 0)
                {
                    if (index >= ((List<MyPhysicalInventoryItem>)__instance.easyGetField("m_items")).Count && index < __instance.MaxItemCount)
                    {
                        amount = (MyFixedPoint)__instance.easyCallMethod("AddItemsToNewStack", new Object[]{amount, maxStack, objectBuilder, itemId, index});
                        flag = true;
                    }
                    else if (index < ((List<MyPhysicalInventoryItem>)__instance.easyGetField("m_items")).Count)
                    {
                        if (((List<MyPhysicalInventoryItem>)__instance.easyGetField("m_items"))[index].Content.CanStack(objectBuilder))
                            amount = (MyFixedPoint) __instance.easyCallMethod("AddItemsToExistingStack", new Object[]{index, amount, maxStack});
                        else if (((List<MyPhysicalInventoryItem>)__instance.easyGetField("m_items")).Count < __instance.MaxItemCount)
                        {
                            amount = (MyFixedPoint)__instance.easyCallMethod("AddItemsToNewStack", new Object[]{amount, maxStack, objectBuilder, itemId, index});
                            flag = true;
                        }
                    }
                }
        
                for (int index1 = 0; index1 < __instance.MaxItemCount; ++index1)
                {
                    if (index1 < ((List<MyPhysicalInventoryItem>)__instance.easyGetField("m_items")).Count)
                    {
                        MyPhysicalInventoryItem physicalInventoryItem = ((List<MyPhysicalInventoryItem>)__instance.easyGetField("m_items"))[index1];
                        if (physicalInventoryItem.Content.CanStack(objectBuilder))
                        {
                            __instance.RaiseContentsAdded(physicalInventoryItem, amount);
                            amount = (MyFixedPoint) __instance.easyCallMethod("AddItemsToExistingStack", new Object[]{index1, amount, maxStack});
                            __instance.RaiseInventoryContentChanged(physicalInventoryItem, amount);
                        }
                    }
                    else
                    {
                        amount = (MyFixedPoint)__instance.easyCallMethod("AddItemsToNewStack", new Object[]{amount, maxStack,
                            flag ? (MyObjectBuilder_PhysicalObject) objectBuilder.Clone() : objectBuilder, itemId, index});
                        flag = true;
                    }
        
                    if (amount == (MyFixedPoint) 0)
                        break;
                }
        
                __instance.easyCallMethod("RefreshVolumeAndMass", new object[0]);
                __instance.OnContentsChanged();
            }
            catch (Exception e)
            {
                Log.Error("Exception in during MyShipDrillShootPatch", e);
            }

            return false;
        }
    }
}