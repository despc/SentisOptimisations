using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.CodeAnalysis;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using SentisOptimisationsPlugin.CrashFix;
using Torch.Managers.PatchManager;
using Torch.Utils;
using VRage.Game.Entity;
using VRage.Network;
using VRage.Scripting.CompilerMethods;

namespace FixTurrets.Perfomance
{
    [PatchShim]
    public static class ParallelUpdateTweaks
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        [ReflectedGetter(Name = "m_dataByFuelType")]
        private static Func<MyEntityThrustComponent, List<MyEntityThrustComponent.FuelTypeData>> _dataByFuelType;
        
        private static Type MyThrusterBlockThrustComponentType =
            typeof(MyParallelEntityUpdateOrchestrator).Assembly.GetType(
                "Sandbox.Game.EntityComponents.MyThrusterBlockThrustComponent");
        
        private static MethodInfo GetEntityMethod = MyThrusterBlockThrustComponentType.GetProperty("Entity",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).GetMethod;
        
        
        
        public static void Patch(PatchContext ctx)
        {
            
            var MethodThrustUpdateBeforeSimulation = MyThrusterBlockThrustComponentType.GetMethod
                ("UpdateThrusts", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            // ctx.GetPattern(MethodThrustUpdateBeforeSimulation).Prefixes.Add(
            //     typeof(ParallelUpdateTweaks).GetMethod(nameof(UpdateThrustsPatched),
            //         BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodSoundEmitterUpdate = typeof(MyEntity3DSoundEmitter).GetMethod
                (nameof(MyEntity3DSoundEmitter.Update), BindingFlags.Instance | BindingFlags.Public);


            var perfCountingRewriter = typeof(ModPerfCounter).Assembly.GetType("VRage.Scripting.Rewriters.PerfCountingRewriter");
            var MethodRewrite = perfCountingRewriter.GetMethod
                ("Rewrite", BindingFlags.Static | BindingFlags.Public);
            
            ctx.GetPattern(MethodRewrite).Prefixes.Add(
                typeof(ParallelUpdateTweaks).GetMethod(nameof(RewritePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            
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
            
            var MethodUpdateAutopilot = typeof(MyAutopilotComponent).GetMethod
                (nameof(MyAutopilotComponent.UpdateAutopilot), BindingFlags.Instance | BindingFlags.Public);
            CrashFixPatch.harmony.Patch(MethodUpdateAutopilot, finalizer: new HarmonyMethod(finalizer));
        }

        private static bool UpdateThrustsPatched(MyEntityThrustComponent __instance)
        {
            try
            {
                MyCubeGrid grid = (MyCubeGrid)__instance.Entity;

                if (grid == null)
                {
                    return false;
                }

                if (grid.IsStatic)
                {
                    var fuelTypeDatas = _dataByFuelType.Invoke(__instance);
                    foreach (var fuelTypeData in fuelTypeDatas)
                    {
                        if (fuelTypeData.CurrentRequiredFuelInput > 0.0)
                        {
                            fuelTypeData.CurrentRequiredFuelInput = 0;
                        }
                    }
                }
                return false;
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
        
        private static bool RewritePatched(SyntaxTree syntaxTree, ref SyntaxTree __result)
        {
            __result = syntaxTree;
            return false;
        }
    }
}