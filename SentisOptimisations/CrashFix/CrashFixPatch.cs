using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NAPI;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.Weapons;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRage.Scripting;
using VRage.Sync;

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
            
            var MethodCreateCompilation = typeof(MyScriptCompiler).GetMethod
                ("CreateCompilation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            
            ctx.GetPattern(MethodCreateCompilation).Prefixes.Add(
                typeof(CrashFixPatch).GetMethod(nameof(CreateCompilationPatched),
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
        
        private static bool CreateCompilationPatched(MyScriptCompiler __instance, string assemblyFileName,
            IEnumerable<Script> scripts,
            bool enableDebugInformation, ref CSharpCompilation  __result)
        {
            try
            {
                var m_runtimeCompilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, platform: Platform.X64);
                var m_conditionalParseOptions = new CSharpParseOptions(LanguageVersion.CSharp6, DocumentationMode.None);
                IEnumerable<SyntaxTree> syntaxTrees = (IEnumerable<SyntaxTree>) null;
                if (scripts != null)
                {
                    CSharpParseOptions parseOptions = m_conditionalParseOptions.WithPreprocessorSymbols((IEnumerable<string>) __instance.ConditionalCompilationSymbols);
                    syntaxTrees = scripts.Select<Script, SyntaxTree>((Func<Script, SyntaxTree>) (s => CSharpSyntaxTree.ParseText(s.Code, parseOptions, s.Name, Encoding.UTF8)));
                }

                string assemblyName = (string) ReflectionUtils.InvokeStaticMethod(typeof(MyScriptCompiler), "MakeAssemblyName", new []{assemblyFileName});
                __result = CSharpCompilation.Create(assemblyName, syntaxTrees, (IEnumerable<MetadataReference>) __instance.easyGetField("m_metadataReferences"), m_runtimeCompilationOptions);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error("CreateCompilation exception", e);
                return false;
                
            }

            return false;
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