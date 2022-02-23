using System.Reflection;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRage.Sync;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.SessionComponents;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisOptimisationsPlugin.CrashFix
{
    [PatchShim]
    public static class CrashFixPatch
    {
        private static Harmony harmony = new Harmony("SentisOptimisationsPlugin.CrashFix");

        private static MethodInfo original = typeof(Sync<MyTurretTargetFlags, SyncDirection.BothWays>).GetMethod
            ("IsValid", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        private static MethodInfo prefix = typeof(CrashFixPatch).GetMethod(nameof(MethodIsValidPatched),
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
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
 
            harmony.Patch(original, new HarmonyMethod(prefix));

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