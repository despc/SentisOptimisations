using System;
using System.Reflection;
using HarmonyLib;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using SentisOptimisationsPlugin.CrashFix;
using Torch.Managers.PatchManager;

namespace FixTurrets.Perfomance
{
    [PatchShim]
    public class ParallelUpdateTweaks
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static readonly Random r = new Random();

        private static Type MyThrusterBlockThrustComponentType =
            typeof(MyParallelEntityUpdateOrchestrator).Assembly.GetType(
                "Sandbox.Game.EntityComponents.MyThrusterBlockThrustComponent");
        
        private static MethodInfo GetEntityMethod = MyThrusterBlockThrustComponentType.GetProperty("Entity",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).GetMethod;
        
        public static void Patch(PatchContext ctx)
        {
            
            var MethodThrustUpdateBeforeSimulation = MyThrusterBlockThrustComponentType.GetMethod
                ("UpdateBeforeSimulation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            ctx.GetPattern(MethodThrustUpdateBeforeSimulation).Prefixes.Add(
                typeof(ParallelUpdateTweaks).GetMethod(nameof(MethodThrustUpdateBeforeSimulationPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodSoundEmitterUpdate = typeof(MyEntity3DSoundEmitter).GetMethod
                (nameof(MyEntity3DSoundEmitter.Update), BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodSoundEmitterUpdate).Prefixes.Add(
                typeof(ParallelUpdateTweaks).GetMethod(nameof(Disabled),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            
            var MethodRenderUpdate = typeof(MyThrust).GetMethod
                ("RenderUpdate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            ctx.GetPattern(MethodRenderUpdate).Prefixes.Add(
                typeof(ParallelUpdateTweaks).GetMethod(nameof(Disabled),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodUpdateHeadAndWeapon = typeof(MyCharacter).GetMethod
                ("UpdateHeadAndWeapon", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodUpdateHeadAndWeapon).Prefixes.Add(
                typeof(ParallelUpdateTweaks).GetMethod(nameof(Disabled),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodSetDefaultTexture = typeof(MyTextPanelComponent).GetMethod
                (nameof(MyTextPanelComponent.SetDefaultTexture), BindingFlags.Instance | BindingFlags.Public);

            var finalizer = typeof(CrashFixPatch).GetMethod(nameof(CrashFixPatch.SuppressExceptionFinalizer),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            CrashFixPatch.harmony.Patch(MethodSetDefaultTexture, finalizer: new HarmonyMethod(finalizer));
        }

        private static bool MethodThrustUpdateBeforeSimulationPatched(Object __instance)
        {
            try
            {
                MyCubeGrid grid = (MyCubeGrid)GetEntityMethod.Invoke(__instance, new object[] { });

                if (grid == null || grid.IsStatic)
                {
                    return false;
                }

            }
            catch (Exception e)
            {
                Log.Error("ThrustUpdate exception ", e);
            }
            
            return true;
        }
        
        private static bool Disabled()
        {
            return false;
        }
    }
}