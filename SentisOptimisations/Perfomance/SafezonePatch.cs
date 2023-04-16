using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Havok;
using NLog;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Components;
using VRage.Groups;
using VRage.ModAPI;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class SafezonePatch
    {
        public static Dictionary<long, long> entitiesInSZ = new Dictionary<long, long>();
        public static Dictionary<long, int> Cooldowns = new Dictionary<long, int>();
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var MethodPhantom_Leave = typeof(MySafeZone).GetMethod
                ("phantom_Leave", BindingFlags.Instance | BindingFlags.NonPublic);
            
            ctx.GetPattern(MethodPhantom_Leave).Prefixes.Add(
                typeof(SafezonePatch).GetMethod(nameof(MethodPhantom_LeavePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            
            var MySafeZoneIsSafe = typeof(MySafeZone).GetMethod
                ("IsSafe", BindingFlags.Instance | BindingFlags.NonPublic);
            
            ctx.GetPattern(MySafeZoneIsSafe).Prefixes.Add(
                typeof(SafezonePatch).GetMethod(nameof(MySafeZoneIsSafePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MySafeZoneUpdateBeforeSimulation = typeof(MySafeZone).GetMethod
                (nameof(MySafeZone.UpdateBeforeSimulation), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            ctx.GetPattern(MySafeZoneUpdateBeforeSimulation).Prefixes.Add(
                typeof(SafezonePatch).GetMethod(nameof(UpdateBeforeSimulationPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MyRemoveEntityPhantom = typeof(MySafeZone).GetMethod
                ("RemoveEntityPhantom", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MyRemoveEntityPhantom).Prefixes.Add(
                typeof(SafezonePatch).GetMethod(nameof(MyRemoveEntityPhantomPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));


            enumStringMapping["NotSafe"] = SubgridCheckResult.NOT_SAFE;
            enumStringMapping["NeedExtraCheck"] = SubgridCheckResult.NEED_EXTRA_CHECK;
            enumStringMapping["Safe"] = SubgridCheckResult.SAFE;
            enumStringMapping["Admin"] = SubgridCheckResult.ADMIN;
        }


        private static bool MethodPhantom_LeavePatched(MySafeZone __instance, HkPhantomCallbackShape sender,
            HkRigidBody body)
        {
            try
            {
                IMyEntity entity = body.GetEntity(0U);
                if (entity == null)
                    return false;
                var stopwatch = Stopwatch.StartNew();
                ReflectionUtils.InvokeInstanceMethod(__instance.GetType(), __instance, "RemoveEntityPhantom",
                    new object[] {body, entity});
                stopwatch.Stop();
                var stopwatchElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                if (stopwatchElapsedMilliseconds < SentisOptimisationsPlugin.Config.SafeZonePhysicsThreshold)
                {
                    return false;
                }

                if (entitiesInSZ.ContainsKey(entity.EntityId))
                {
                    entitiesInSZ[entity.EntityId] = entitiesInSZ[entity.EntityId] + stopwatchElapsedMilliseconds;
                }
                else
                {
                    entitiesInSZ[entity.EntityId] = stopwatchElapsedMilliseconds;
                }
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn("MethodPhantom_LeavePatched Exception ", e);
            }

            return false;
        }

        private static bool MyRemoveEntityPhantomPatched(MySafeZone __instance, HkRigidBody body, IMyEntity entity)
        {
            if (!SentisOptimisationsPlugin.Config.RemoveEntityPhantomPatch)
            {
                return true;
            }

            if (MySandboxGame.Static.SimulationFrameCounter < 1000)
            {
                return true;
            }

            try
            {
                MyEntity topEntity = entity.GetTopMostParent() as MyEntity;
                if (topEntity.Physics == null || topEntity.Physics.ShapeChangeInProgress || topEntity != entity)
                    return false;
                bool addedOrRemoved =
                    MySessionComponentSafeZones.IsRecentlyAddedOrRemoved(topEntity) || !entity.InScene;
                Tuple<HkRigidBody, IMyEntity> p = new Tuple<HkRigidBody, IMyEntity>(body, entity);
                HashSet<Tuple<HkRigidBody, IMyEntity>> m_RemoveEntityPhantomTaskList = new HashSet<Tuple<HkRigidBody, IMyEntity>>();
                if (m_RemoveEntityPhantomTaskList.Contains(p))
                    return false;
                m_RemoveEntityPhantomTaskList.Add(p);
                Vector3D position1 = entity.Physics.ClusterToWorld(body.Position);
                Quaternion rotation1 = Quaternion.CreateFromRotationMatrix(body.GetRigidBodyMatrix());
                MySandboxGame.Static.Invoke((Action) (() =>
                {
                    try
                    {
                        m_RemoveEntityPhantomTaskList.Remove(p);
                        if (__instance.Physics == null)
                            return;
                        if (entity.MarkedForClose)
                        {
                            bool RemoveEntityInternalResult = (bool) ReflectionUtils.InvokeInstanceMethod(
                                typeof(MySafeZone), __instance, "RemoveEntityInternal",
                                new object[] {topEntity, addedOrRemoved});
                            if (!RemoveEntityInternalResult)
                                return;
                            ReflectionUtils.InvokeInstanceMethod(typeof(MySafeZone), __instance, "SendRemovedEntity",
                                new object[] {topEntity.EntityId, addedOrRemoved});
                        }
                        else
                        {
                            bool flag = (entity is MyCharacter myCharacter ? (myCharacter.IsDead ? 1 : 0) : 0) != 0 ||
                                        body.IsDisposed || !entity.Physics.IsInWorld;
                            if (entity.Physics != null && !flag)
                            {
                                position1 = entity.Physics.ClusterToWorld(body.Position);
                                rotation1 = Quaternion.CreateFromRotationMatrix(body.GetRigidBodyMatrix());
                            }

                            Vector3D position = __instance.PositionComp.GetPosition();
                            MatrixD matrix = __instance.PositionComp.GetOrientation();
                            Quaternion fromRotationMatrix = Quaternion.CreateFromRotationMatrix(in matrix);
                            HkShape shape1 = HkShape.Empty;
                            if (entity.Physics != null)
                            {
                                if ((HkReferenceObject) entity.Physics.RigidBody != (HkReferenceObject) null)
                                    shape1 = entity.Physics.RigidBody.GetShape();
                                else if (entity.Physics is MyPhysicsBody physics && (entity as MyCharacter != null) &&
                                         physics.CharacterProxy != null)
                                    shape1 = physics.CharacterProxy.GetHitRigidBody().GetShape();
                            }

                            bool isPenetratingShapeShape;

                                isPenetratingShapeShape = MyPhysics.IsPenetratingShapeShape(shape1, ref position1, ref rotation1,
                                    __instance.Physics.RigidBody.GetShape(), ref position, ref fromRotationMatrix);
                            
                            if ((flag ? 1 : (shape1.IsValid ? (!isPenetratingShapeShape ? 1 : 0) : 1)) == 0)
                                return;
                            bool RemoveEntityInternalResult = (bool) ReflectionUtils.InvokeInstanceMethod(
                                typeof(MySafeZone), __instance, "RemoveEntityInternal",
                                new object[] {topEntity, addedOrRemoved});
                            if (RemoveEntityInternalResult)
                            {
                                ReflectionUtils.InvokeInstanceMethod(typeof(MySafeZone), __instance,
                                    "SendRemovedEntity",
                                    new object[] {topEntity.EntityId, addedOrRemoved});
                                if (topEntity is MyCubeGrid myCubeGrid)
                                {
                                    foreach (MyShipController fatBlock in myCubeGrid.GetFatBlocks<MyShipController>())
                                    {
                                        if (!(fatBlock is MyRemoteControl) && fatBlock.Pilot != null &&
                                            (fatBlock.Pilot != topEntity &&
                                             (bool) ReflectionUtils.InvokeInstanceMethod(typeof(MySafeZone), __instance,
                                                 "RemoveEntityInternal",
                                                 new object[] {(MyEntity) fatBlock.Pilot, addedOrRemoved})))
                                        {
                                            ReflectionUtils.InvokeInstanceMethod(typeof(MySafeZone), __instance,
                                                "SendRemovedEntity",
                                                new object[] {fatBlock.Pilot.EntityId, addedOrRemoved});
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error("Phantom leave exception " + e);
                    }
                }), "Phantom leave");
            }
            catch (Exception e)
            {
                Log.Error("MyRemoveEntityPhantomPatched error " + e);
            }
            return false;
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

            Cooldowns[blockId] = 0;
            return true;
        }

        private static bool UpdateBeforeSimulationPatched(MySafeZone __instance)
        {
            var safeZoneBlockId = __instance.SafeZoneBlockId;
            if (safeZoneBlockId == 0)
            {
                return true;
            }
            MyCubeBlock entity;
            if (!MyEntities.TryGetEntityById<MyCubeBlock>(safeZoneBlockId, out entity))
            {
                return true;
            }

            var entityCubeGrid = entity.CubeGrid;
            if (SentisOptimisationsPlugin.Config.SlowdownEnabled)
            {
                    var myUpdateTiersPlayerPresence = entityCubeGrid.PlayerPresenceTier;
                    if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier1)
                    {
                        if (NeedSkip(entityCubeGrid.EntityId, 10)) return false;
                    }
                    else if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier2)
                    {
                        if (NeedSkip(entityCubeGrid.EntityId, 100)) return false;
                    }   
            }

            return true;
        }

        private static bool MySafeZoneIsSafePatched(MySafeZone __instance, MyEntity entity, ref bool __result)
        {

            if (!SentisOptimisationsPlugin.Config.SafeZoneSubGridOptimisation)
            {
                return true;
            }
            
            if (SentisOptimisationsPlugin.Config.SlowdownEnabled)
            {
                var myCubeGrid = entity as MyCubeGrid;
                if (myCubeGrid != null)
                {
                    var myUpdateTiersPlayerPresence = myCubeGrid.PlayerPresenceTier;
                    if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier1)
                    {
                        if (NeedSkip(myCubeGrid.EntityId, 10))
                        {
                            __result = true;
                            return false;
                        }
                    }
                    else if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier2)
                    {
                        if (NeedSkip(myCubeGrid.EntityId, 100))
                        {
                            __result = true;
                            return false;
                        }
                    }   
                }
                
            }
            
            try
            {
                MyFloatingObject myFloatingObject = entity as MyFloatingObject;
                MyInventoryBagEntity inventoryBagEntity = entity as MyInventoryBagEntity;
                if (myFloatingObject != null || inventoryBagEntity != null)
                {
                    __result = __instance.Entities.Contains(entity.EntityId)
                        ? __instance.AccessTypeFloatingObjects == MySafeZoneAccess.Whitelist
                        : (uint) __instance.AccessTypeFloatingObjects > 0U;
                    return false;
                }

                MyEntity topMostParent = entity.GetTopMostParent((System.Type) null);
                MyIDModule component;
                if (topMostParent is IMyComponentOwner<MyIDModule> myComponentOwner &&
                    myComponentOwner.GetComponent(out component))
                {
                    ulong steamId = MySession.Static.Players.TryGetSteamId(component.Owner);
                    if (steamId != 0UL && MySafeZone.CheckAdminIgnoreSafezones(steamId))
                    {
                        __result = true;
                        return false;  
                    }
                        
                    if (__instance.AccessTypePlayers == MySafeZoneAccess.Whitelist)
                    {
                        if (__instance.Players.Contains(component.Owner))
                        {
                            __result = true;
                            return false;  
                        }
                            
                    }
                    else if (__instance.Players.Contains(component.Owner))
                    {
                        __result = false;
                        return false; 
                    }
                        

                    if (MySession.Static.Factions.TryGetPlayerFaction(component.Owner) is MyFaction playerFaction)
                    {
                        if (__instance.AccessTypeFactions == MySafeZoneAccess.Whitelist)
                        {
                            if (__instance.Factions.Contains(playerFaction))
                            {
                                __result = true;
                                return false; 
                            }

                        }
                        else if (__instance.Factions.Contains(playerFaction))
                        {
                            __result = false;
                            return false; 
                        }
                    }

                    __result = __instance.AccessTypePlayers == MySafeZoneAccess.Blacklist;
                    return false;
                }

                if (topMostParent is MyCubeGrid nodeInGroup)
                {
                    MyGroupsBase<MyCubeGrid> groups = MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Mechanical);
                   SubgridCheckResult subgridCheckResult1 = SubgridCheckResult.NOT_SAFE;
                    
                    foreach (MyCubeGrid groupNode in groups.GetGroupNodes(nodeInGroup))
                    {
                        object result = ReflectionUtils.InvokeInstanceMethod(typeof(MySafeZone), __instance, "IsSubGridSafe", new []{groupNode});
                        SubgridCheckResult subgridCheckResult2 = MapToResult(result);
                        //MySafeZone.SubgridCheckResult subgridCheckResult2 = __instance.IsSubGridSafe(groupNode);
                        switch (subgridCheckResult2)
                        {
                            case SubgridCheckResult.NOT_SAFE:
                                subgridCheckResult1 = SubgridCheckResult.NOT_SAFE;
                                continue;
                            case SubgridCheckResult.NEED_EXTRA_CHECK:
                                if (subgridCheckResult2 > subgridCheckResult1)
                                {
                                    subgridCheckResult1 = subgridCheckResult2;
                                }
                                continue;
                            case SubgridCheckResult.SAFE:
                            case SubgridCheckResult.ADMIN:
                                __result = true;
                                return false;
                            default:
                                continue;
                        }
                    }

                    __result = false;
                    return false;
                }

                switch (entity)
                {
                    case MyAmmoBase _:
                    case MyMeteor _:
                        if ((__instance.AllowedActions & MySafeZoneAction.Shooting) == (MySafeZoneAction) 0)
                        {
                            __result = false;
                            return false;
                        }
                            
                        break;
                }

                __result = true;
                return false;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn("MySafeZoneIsSafePatched Exception ", e);
            }

            return false;
        }

        private static SubgridCheckResult MapToResult(object result)
        {
            SubgridCheckResult response;
            if (enumMapping.TryGetValue(result, out response))
            {
                return response;
            }

            response = enumStringMapping[result.ToString()];
            enumMapping[result] = response;
            return response;
        }

        private enum SubgridCheckResult
        {
            NOT_SAFE,
            NEED_EXTRA_CHECK,
            SAFE,
            ADMIN
        }
        private  static Dictionary<string, SubgridCheckResult> enumStringMapping = new Dictionary<string, SubgridCheckResult>();
        private  static Dictionary<object, SubgridCheckResult> enumMapping = new Dictionary<object, SubgridCheckResult>();
    }
}