using System;
using System.Reflection;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRage.Sync;
using HarmonyLib;
using NAPI;
using NLog.Fluent;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.SessionComponents;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisOptimisationsPlugin.CrashFix
{
    [PatchShim]
    public static class CrashFixPatch
    {
        // private static Harmony harmony = new Harmony("SentisOptimisationsPlugin.CrashFix");

        // private static MethodInfo original = typeof(Sync<MyTurretTargetFlags, SyncDirection.BothWays>).GetMethod
        //     ("IsValid", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        // private static MethodInfo prefix = typeof(CrashFixPatch).GetMethod(nameof(MethodIsValidPatched),
        //     BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
        public static void Patch(PatchContext ctx)
        {
            
            
            var MethodPistonInit = typeof(MyPistonBase).GetMethod
                (nameof(MyPistonBase.Init), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

            
            ctx.GetPattern(MethodPistonInit).Prefixes.Add(
                typeof(CrashFixPatch).GetMethod(nameof(MethodPistonInitPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodCreateLightning = typeof(MySectorWeatherComponent).GetMethod
                (nameof(MySectorWeatherComponent.CreateLightning), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

            
            ctx.GetPattern(MethodCreateLightning).Prefixes.Add(
                typeof(CrashFixPatch).GetMethod(nameof(CreateLightningPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            // var MethodUpdateBeforeSimulation10 = typeof(MyShipDrill).GetMethod
            //     (nameof(MyShipDrill.UpdateBeforeSimulation10), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            //
            //
            // ctx.GetPattern(MethodUpdateBeforeSimulation10).Prefixes.Add(
            //     typeof(CrashFixPatch).GetMethod(nameof(UpdateBeforeSimulation10Patched),
            //         BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
 
            // harmony.Patch(original, new HarmonyMethod(prefix));

        }
        
        
        private static bool MethodIsValidPatched(MyTurretTargetFlags value, ref bool __result)
        {
            __result = true;
            return false;
        }
        private static void MethodPistonInitPatched(MyPistonBase __instance)
        {
            __instance.Velocity.ValueChanged += VelocityOnValueChanged;
        }
        
        private static bool CreateLightningPatched()
        {
            if (SentisOptimisationsPlugin.Config.DisableLightnings)
            {
                return false;
            }

            return true;
        }

        private static bool UpdateBeforeSimulation10Patched(MyShipDrill __instance)
        {
            try
            {
                __instance.easyCallMethod("Receiver_IsPoweredChanged", new object[0]);
                __instance.UpdateBeforeSimulation10();
                if (__instance.Parent == null || __instance.Parent.Physics == null)
                    return false;
                __instance.easySetField("m_drillFrameCountdown", 10);
                if ((int)__instance.easyGetField("m_drillFrameCountdown") > 0)
                    return false;
                __instance.easySetField("m_drillFrameCountdown", 90);
                if (__instance.CanShoot(MyShootActionEnum.PrimaryAction, __instance.OwnerId, out MyGunStatusEnum _))
                {
                    if (((MyDrillBase) __instance.easyGetField("m_drillBase")).Drill(
                            __instance.Enabled || (bool) __instance.easyGetField("m_wantsToCollect"),
                            speedMultiplier: 0.1f))
                    {
                    }
                    // __instance.ShakeAmount = 1f;
                    else
                    {
                    }
                    // __instance.ShakeAmount = 0.5f;
                }
                else
                {
                }
                // __instance.ShakeAmount = 0.0f;
            }
            catch (Exception e)
            {
                Log.Error("I LOVE KEEN! Crash prevented " + e);
            }
            return false;
        }


        private static void VelocityOnValueChanged(SyncBase obj)
        {
            var sync = ((Sync<float, SyncDirection.BothWays>) obj);
            float value = sync.Value;
            if (float.IsNaN(value))
            {
                sync.Value = 0;
            }
        }
    }
}