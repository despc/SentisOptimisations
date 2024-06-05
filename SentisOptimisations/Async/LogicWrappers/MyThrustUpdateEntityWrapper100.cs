using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using NLog.Fluent;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Utils;

namespace SentisOptimisationsPlugin.Async;

[HarmonyPatch]
public class MyThrustUpdateEntityWrapper100 : UpdateEntityWrapper
{
    private static MethodInfo _checkIsWorking = typeof(MyThrust).GetMethod("CheckIsWorking", BindingFlags.Instance |
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

    private static MethodInfo _isWorkingPropSetMethod = typeof(MyCubeBlock).GetProperty("IsWorking",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetSetMethod(true);

    private static FieldInfo _isWorkingChangedEvent = typeof(MyCubeBlock)
        .GetField("IsWorkingChanged", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static FieldInfo _connectedGroupsField = typeof(MyEntityThrustComponent).GetField("m_connectedGroups",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    public MyThrustUpdateEntityWrapper100()
    {
        EntityType = typeof(MyThrust);
        var methodUpdateIsWorking = typeof(MyCubeBlock).GetMethod
        ("UpdateIsWorking",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

        HarmonyInstance.Patch(methodUpdateIsWorking, prefix: new HarmonyMethod(
            typeof(MyThrustUpdateEntityWrapper100).GetMethod(nameof(DisabledForThrust),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)));

        var methodFindEntityGroup = typeof(MyEntityThrustComponent).GetMethod
        ("FindEntityGroup",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

        HarmonyInstance.Patch(methodFindEntityGroup, prefix: new HarmonyMethod(
            typeof(MyThrustUpdateEntityWrapper100).GetMethod(nameof(FindEntityGroupPatch),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)));
    }

    public static bool FindEntityGroupPatch(MyEntityThrustComponent __instance, MyEntity thrustEntity,
        ref MyEntityThrustComponent.MyConveyorConnectedGroup __result)
    {
        if (!SentisOptimisationsPlugin.Config.AsyncLogicUpdateMain)
        {
            return true;
        }
        
        if (!(thrustEntity is MyThrust))
        {
            return true;
        }

        if (Thread.CurrentThread == MyUtils.MainThread)
        {
            return true;
        }

        MyEntityThrustComponent.MyConveyorConnectedGroup entityGroup = null;
        try
        {
            List<MyEntityThrustComponent.MyConveyorConnectedGroup> m_connectedGroups =
                (List<MyEntityThrustComponent.MyConveyorConnectedGroup>)_connectedGroupsField.GetValue(__instance);

            MyThrust myThrust = thrustEntity as MyThrust;
            MyDefinitionId fuelId = myThrust.FuelDefinition == null
                ? MyResourceDistributorComponent.ElectricityId
                : myThrust.FuelDefinition.Id;
            if (MyResourceDistributorComponent.IsConveyorConnectionRequiredTotal(fuelId))
            {
                var connectedGroupsSnapshot =
                    new List<MyEntityThrustComponent.MyConveyorConnectedGroup>(m_connectedGroups);
                foreach (MyEntityThrustComponent.MyConveyorConnectedGroup connectedGroup in connectedGroupsSnapshot)
                {
                    int typeIndex;
                    if (connectedGroup.TryGetTypeIndex(ref fuelId, out typeIndex))
                    {
                        var myEntitySets = connectedGroup.DataByFuelType[typeIndex]
                            .ThrustsByDirection.Values;
                        foreach (HashSet<MyEntity> myEntitySet in myEntitySets)
                        {
                            if (myEntitySet.Contains(thrustEntity))
                            {
                                entityGroup = connectedGroup;
                                break;
                            }
                        }

                        if (entityGroup != null)
                            break;
                    }
                }
            }
        }
        catch (Exception e)
        {
            SentisOptimisationsPlugin.Log.Error(e, "FindEntityGroupPatch exception");
        }

        __result = entityGroup;
        return false;
    }

    public static bool DisabledForThrust(MyCubeBlock __instance)
    {
        return !(__instance is MyThrust);
    }

    public override void Update(MyEntity entity)
    {
        var thrust = entity as MyThrust;
        if (thrust == null || thrust.Closed || thrust.MarkedForClose)
        {
            return;
        }

        bool isWorkingNew = (bool)_checkIsWorking.Invoke(thrust, new object[] { });
        if (isWorkingNew == thrust.IsWorking)
        {
            return;
        }

        _isWorkingPropSetMethod.Invoke(thrust, [isWorkingNew]);

        var eventDelegate = (MulticastDelegate)_isWorkingChangedEvent.GetValue(thrust);
        if (eventDelegate != null)
        {
            MyAPIGateway.Utilities.InvokeOnGameThread((Action)(() =>
            {
                try
                {
                    foreach (var handler in eventDelegate.GetInvocationList())
                    {
                        handler.Method.Invoke(handler.Target, new object[] { thrust });
                    }
                }
                catch (Exception ex)
                {
                    SentisOptimisationsPlugin.Log.Error(ex, "Raise on thrust working exception");
                }
            }));
        }
    }
}