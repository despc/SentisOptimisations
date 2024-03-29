﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NAPI;
using Sandbox.Game;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Replication.StateGroups;
using Sandbox.Game.Weapons;
using SentisOptimisations;
using SpaceEngineers.Game.EntityComponents.Blocks;
using Torch.Managers.PatchManager;
using VRage.Network;
using VRage.Scripting;
using VRage.Sync;

namespace SentisOptimisationsPlugin.CrashFix
{
    [PatchShim]
    public static class CrashFixPatch
    {
        public static Harmony harmony = new Harmony("CrashFixPatch");
        public static void Patch(PatchContext ctx)
        {
            
            var MethodPistonInit = typeof(MyPistonBase).GetMethod
                (nameof(MyPistonBase.Init), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

            
            ctx.GetPattern(MethodPistonInit).Prefixes.Add(
                typeof(CrashFixPatch).GetMethod(nameof(MethodPistonInitPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodCreateCompilation = typeof(MyScriptCompiler).GetMethod
                ("CreateCompilation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            
            var MethodApplyDirtyGroups = typeof(MyReplicationServer).GetMethod
                ("ApplyDirtyGroups", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            
            ctx.GetPattern(MethodCreateCompilation).Prefixes.Add(
                typeof(CrashFixPatch).GetMethod(nameof(CreateCompilationPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodSetDetailedInfo = typeof(MyProgrammableBlock).GetMethod
                ("SetDetailedInfo", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            
            var MethodRemoveIdentity = typeof(MyPlayerCollection).GetMethod
                ( nameof(MyPlayerCollection.RemoveIdentity), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodUpdateWaypointPositions = typeof(MyOffensiveCombatCircleOrbit).GetMethod
                ("UpdateWaypointPositions", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            var MethodNotify = typeof(MyPropertySyncStateGroup).GetMethod
                ("Notify", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodMyExplosionsUpdateBeforeSimulation = typeof(MyExplosions).GetMethod
                ("UpdateBeforeSimulation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodMyAngleGrinderGrind = typeof(MyAngleGrinder).GetMethod
                ("Grind", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodOnRegisteredToThrustComponent = typeof(Sandbox.Game.Entities.MyThrust).GetMethod
                ("OnRegisteredToThrustComponent", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                
            var finalizer = typeof(CrashFixPatch).GetMethod(nameof(SuppressExceptionFinalizer),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            harmony.Patch(MethodSetDetailedInfo, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodUpdateWaypointPositions, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodNotify, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodMyExplosionsUpdateBeforeSimulation, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodMyAngleGrinderGrind, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodOnRegisteredToThrustComponent, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodRemoveIdentity, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodApplyDirtyGroups, finalizer: new HarmonyMethod(finalizer));
            
        }


        public static Exception SuppressExceptionFinalizer(Exception __exception)
        {
            // if (__exception != null)
            // {
                // SentisOptimisationsPlugin.Log.Error("SuppressException ", __exception);
                // }
            return null;
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