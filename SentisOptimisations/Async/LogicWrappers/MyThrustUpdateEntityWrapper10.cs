using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRage.Utils;

namespace SentisOptimisationsPlugin.Async;

[HarmonyPatch]
public class MyThrustUpdateEntityWrapper10 : UpdateEntityWrapper
{
   public MyThrustUpdateEntityWrapper10()
    {
        EntityType = typeof(MyThrust);
        var methodUpdateThrusterLenght = typeof(MyThrust).GetMethod
        ("UpdateThrusterLenght",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
        
        HarmonyInstance.Patch(methodUpdateThrusterLenght, prefix: new HarmonyMethod(
            typeof(UpdateEntityWrapper).GetMethod(nameof(Disabled),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)));
    }

    public override void Update(MyEntity entity)
    {
        var thrust = entity as MyThrust;
        if (thrust == null || thrust.Closed || thrust.MarkedForClose)
        {
            return;
        }
        thrust.ThrustLengthRand = thrust.CurrentStrength * 10f * MyUtils.GetRandomFloat(0.6f, 1f) *
                                  thrust.BlockDefinition.FlameLengthScale;
    }
}