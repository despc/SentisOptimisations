using System.Reflection;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRage.Sync;
using HarmonyLib;

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
            
            
            var MethodLoadWorld = typeof(MySession).GetMethod
                ("LoadWorld", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            
            ctx.GetPattern(MethodLoadWorld).Prefixes.Add(
                typeof(CrashFixPatch).GetMethod(nameof(LoadWorldPatched),
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
        
        private static void LoadWorldPatched()
        {
            MySession.Static.Settings.PiratePCU = 300000;

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