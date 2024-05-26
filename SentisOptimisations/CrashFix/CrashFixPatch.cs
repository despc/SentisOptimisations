using System;
using System.Collections;
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
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Replication;
using Sandbox.Game.Replication.StateGroups;
using Sandbox.Game.Weapons;
using SentisOptimisations;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.EntityComponents.Blocks;
using Torch.Managers.PatchManager;
using Torch.Utils;
using VRage.Network;
using VRage.Scripting;
using VRage.Sync;

namespace SentisOptimisationsPlugin.CrashFix
{
    [PatchShim]
    public static class CrashFixPatch
    {
        [ReflectedGetter(Name = "m_clientStates")]
        private static Func<MyReplicationServer, IDictionary> _clientStates;
        
        public static Harmony harmony = new Harmony("CrashFixPatch");
        public static void Patch(PatchContext ctx)
        {
            
            var MethodPistonInit = typeof(MyPistonBase).GetMethod
                (nameof(MyPistonBase.Init), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodAssDoUpdateTimerTick = typeof(MyAssembler).GetMethod
                (nameof(MyAssembler.DoUpdateTimerTick), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

            
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
                
            var MethodFleeAwayFromTargetLogic = typeof(MyDefensiveCombatBlock).GetMethod
                ("FleeAwayFromTargetLogic", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodFlee = typeof(MyDefensiveCombatBlock).GetMethod
                ("Flee", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodFleeOnWaypointReached = typeof(MyDefensiveCombatBlock).GetMethod
                ("FleeOnWaypointReached", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodMyCargoContainerInventoryBagReplicableOnSave = typeof(MyCargoContainerInventoryBagReplicable).GetMethod
                ( nameof(MyCargoContainerInventoryBagReplicable.OnSave), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
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
            harmony.Patch(MethodFleeAwayFromTargetLogic, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodAssDoUpdateTimerTick, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodFlee, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodFleeOnWaypointReached, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodMyCargoContainerInventoryBagReplicableOnSave, finalizer: new HarmonyMethod(finalizer));
            
            // var MethodRemoveClient = typeof(MyReplicationServer).GetMethod
            //     ("RemoveClient", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            // ctx.GetPattern(MethodRemoveClient).Prefixes.Add(
            //     typeof(CrashFixPatch).GetMethod(nameof(RemoveClientPatch),
            //         BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }


        public static Exception SuppressExceptionFinalizer(Exception __exception)
        {
            if (__exception != null && SentisOptimisationsPlugin.Config.EnableMainDebugLogs)
            {
                SentisOptimisationsPlugin.Log.Error(__exception, "SuppressedException ");
            }
            return null;
        }
        
        // private static bool RemoveClientPatch(MyReplicationServer __instance, Endpoint endpoint)
        // {
        //     Object client;
        //     var clientDataDict = _clientStates.Invoke(__instance);
        //     if (!clientDataDict.TryGetValue(endpoint, out client))
        //         return false;
        //     while (client.Replicables.Count > 0)
        //         __instance.RemoveForClient(client.Replicables.FirstPair().Key, client, false);
        //     __instance.m_clientStates.Remove<Endpoint, MyClient>(endpoint);
        //     __instance.m_recentClientsStates[endpoint] = this.m_callback.GetUpdateTime() + this.SAVED_CLIENT_DURATION;
        // }
        
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