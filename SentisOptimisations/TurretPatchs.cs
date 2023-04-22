using System.Reflection;
using NAPI;
using NLog;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Gui;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SentisOptimisations;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.Weapons.Guns;
using Torch.Managers.PatchManager;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class FixTurretsPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static void Patch(PatchContext ctx)
        {
            var methodInfo = typeof(MyLargeTurretBase).GetMethod("DoUpdateTimerTick",BindingFlags.DeclaredOnly
                | BindingFlags.Instance | BindingFlags.Public);
            ctx.GetPattern(methodInfo).Prefixes.Add(
                typeof(FixTurretsPatch).GetMethod(nameof(DoUpdateTimerTickPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var methodInfoSearchlight = typeof(MySearchlight).GetMethod("DoUpdateTimerTick",BindingFlags.DeclaredOnly
                | BindingFlags.Instance | BindingFlags.Public);
            ctx.GetPattern(methodInfoSearchlight).Prefixes.Add(
                typeof(FixTurretsPatch).GetMethod(nameof(DoUpdateTimerTickSlPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
                
            var MethodSlUpdateAfter = typeof(MySearchlight).GetMethod("UpdateAfterSimulation",BindingFlags.DeclaredOnly
                | BindingFlags.Instance | BindingFlags.Public);
            ctx.GetPattern(MethodSlUpdateAfter).Prefixes.Add(
                typeof(FixTurretsPatch).GetMethod(nameof(UpdateAfterSimulationPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var methodInfo2 = typeof(MyLargeTurretBase).GetMethod("UpdateAfterSimulation",BindingFlags.DeclaredOnly
                | BindingFlags.Instance | BindingFlags.Public);
            ctx.GetPattern(methodInfo2).Prefixes.Add(
                typeof(FixTurretsPatch).GetMethod(nameof(UpdateAfterSimulationPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var methodUpdateBeforeSimulation100 = typeof(MyLargeConveyorTurretBase).GetMethod("UpdateBeforeSimulation100",BindingFlags.DeclaredOnly
                | BindingFlags.Instance | BindingFlags.Public);
            ctx.GetPattern(methodUpdateBeforeSimulation100).Prefixes.Add(
                typeof(FixTurretsPatch).GetMethod(nameof(UpdateAfterSimulationPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

        }

        private static bool UpdateAfterSimulationPatch(MyLargeTurretBase __instance)
        {
            if (!SentisOptimisationsPlugin.Config.DisableTurretUpdate)
            {
                return true;
            }
            return false;
        }
        
        private static bool DoUpdateTimerTickPatch(MyLargeTurretBase __instance)
        {
            if (!SentisOptimisationsPlugin.Config.DisableTurretUpdate)
            {
                return true;
            }
            // config
            if (__instance.Render.IsVisible() && __instance.IsWorking && __instance.Enabled)
            {
                var myTurretController = ((MyTurretController)ReflectionUtils.GetInstanceField(typeof(MyLargeTurretBase), __instance, "m_turretController"));
                if (myTurretController.IsControlled)
                {
                    if (!myTurretController.IsInRangeAndPlayerHasAccess())
                    {
                        __instance.easyCallMethod("ReleaseControl", new object[]{false});
                        if (MyGuiScreenTerminal.IsOpen && MyGuiScreenTerminal.InteractedEntity == __instance)
                            MyGuiScreenTerminal.Hide();
                    }
                    else
                    {
                        myTurretController.GetFirstRadioReceiver()?.UpdateHud(true);
                        var myGuiScreenHudSpace = MyGuiScreenHudSpace.Static;
                        if (myTurretController.IsControlledByLocalPlayer &&
                            myGuiScreenHudSpace != null)
                        {
                            ReflectionUtils.InvokeInstanceMethod(typeof(MyGuiScreenHudSpace), myGuiScreenHudSpace,
                                "SetToolbarVisible", new object[] { false });
                        }
                    }
                }
                __instance.NeedsUpdate = MyEntityUpdateEnum.EACH_100TH_FRAME;
            }

            return false;
        }
        
        private static bool DoUpdateTimerTickSlPatch(MySearchlight __instance)
        {
            if (!SentisOptimisationsPlugin.Config.DisableTurretUpdate)
            {
                return true;
            }
            // config
            if (__instance.Render.IsVisible() && __instance.IsWorking && __instance.Enabled)
            {
                var myTurretController = ((MyTurretController)ReflectionUtils.GetInstanceField(typeof(MySearchlight), __instance, "m_turretController"));
                if (myTurretController.IsControlled)
                {
                    if (!myTurretController.IsInRangeAndPlayerHasAccess())
                    {
                        __instance.easyCallMethod("ReleaseControl", new object[]{false});
                        if (MyGuiScreenTerminal.IsOpen && MyGuiScreenTerminal.InteractedEntity == __instance)
                            MyGuiScreenTerminal.Hide();
                    }
                    else
                    {
                        myTurretController.GetFirstRadioReceiver()?.UpdateHud(true);
                        var myGuiScreenHudSpace = MyGuiScreenHudSpace.Static;
                        if (myTurretController.IsControlledByLocalPlayer &&
                            myGuiScreenHudSpace != null)
                        {
                            ReflectionUtils.InvokeInstanceMethod(typeof(MyGuiScreenHudSpace), myGuiScreenHudSpace,
                                "SetToolbarVisible", new object[] { false });
                        }
                    }
                }
                __instance.NeedsUpdate = MyEntityUpdateEnum.EACH_100TH_FRAME;
            }

            return false;
        }
    }
}