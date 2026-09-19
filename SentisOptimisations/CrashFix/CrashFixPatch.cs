using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NAPI;
using Sandbox.Game;
using Sandbox.Game.AI;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Replication;
using Sandbox.Game.Replication.StateGroups;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.Weapons;
using SentisOptimisations;
using SpaceEngineers.Game.AI;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.EntityComponents.Blocks;
using Torch.Managers.PatchManager;
using Torch.Utils;
using VRage.Collections;
using VRage.Library.Utils;
using VRage.Network;
using VRage.Replication;
using VRage.Scripting;
using VRage.Sync;

namespace SentisOptimisationsPlugin.CrashFix
{
    [PatchShim]
    public static class CrashFixPatch
    {
        [ReflectedGetter(Name = "m_clientStates")]
        private static Func<MyReplicationServer, IDictionary> _clientStates;
        
        [ReflectedGetter(Name = "m_recentClientsStates")]
        private static Func<MyReplicationServer, ConcurrentDictionary<Endpoint, MyTimeSpan>> _recentClientsStates;
         
        [ReflectedGetter(Name = "m_callback")]
        private static Func<MyReplicationServer, IReplicationServerCallback> _callback;
        
        [ReflectedGetter(TypeName = "VRage.Network.MyClient, VRage", Name = "Replicables")]
        private static Func<object, MyConcurrentDictionary<IMyReplicable, MyReplicableClientData>> _replicables;
        [ReflectedMethod(Name = "RemoveForClient", OverrideTypeNames = new string[] { null, "VRage.Network.MyClient, VRage", null })]
        private static Action<MyReplicationServer, IMyReplicable, object, bool> _removeForClient;
        
        public static Harmony harmony = new Harmony("CrashFixPatch");

        private static Object RemoveClientLock = new Object();
        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("CrashFixPatch", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            
            var MethodPistonInit = typeof(MyPistonBase).GetMethod
                (nameof(MyPistonBase.Init), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodAssDoUpdateTimerTick = typeof(MyAssembler).GetMethod
                (nameof(MyAssembler.DoUpdateTimerTick), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

            var MethodRefineryDoUpdateTimerTick = typeof(MyRefinery).GetMethod
                (nameof(MyRefinery.DoUpdateTimerTick), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodWolfTargetAttack = typeof(MyWolfTarget).GetMethod
                (nameof(MyWolfTarget.Attack), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodAiGotoTarget = typeof(MyAiTargetBase).GetMethod
                (nameof(MyAiTargetBase.GotoTarget), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            ctx.GetPattern(MethodPistonInit).Prefixes.Add(
                typeof(CrashFixPatch).GetMethod(nameof(MethodPistonInitPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodCreateCompilation = typeof(MyScriptCompiler).GetMethod
                ("CreateCompilation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            
            var MethodCreateVoxelMap = typeof(MyPlanet).GetMethod
                ("CreateVoxelMap", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            
            var MethodApplyDirtyGroups = typeof(MyReplicationServer).GetMethod
                ("ApplyDirtyGroups", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            
            ctx.GetPattern(MethodCreateCompilation).Prefixes.Add(
                typeof(CrashFixPatch).GetMethod(nameof(CreateCompilationPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodSetDetailedInfo = typeof(MyProgrammableBlock).GetMethod
                ("SetDetailedInfo", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            
            var MethodRemoveIdentity = typeof(MyPlayerCollection).GetMethod
                ( nameof(MyPlayerCollection.RemoveIdentity), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodOrchestratorDispatchBeforeSimulation = typeof(MyParallelEntityUpdateOrchestrator).GetMethod
                ("DispatchBeforeSimulation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodOrchestratorDispatchAfterSimulation = typeof(MyParallelEntityUpdateOrchestrator).GetMethod
                ("DispatchAfterSimulation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodUpdateWaypointPositions = typeof(MyOffensiveCombatCircleOrbit).GetMethod
                ("UpdateWaypointPositions", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            var MethodNotify = typeof(MyPropertySyncStateGroup).GetMethod
                ("Notify", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodMyExplosionsUpdateBeforeSimulation = typeof(MyExplosions).GetMethod
                ("UpdateBeforeSimulation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodMyAngleGrinderGrind = typeof(MyAngleGrinder).GetMethod
                ("Grind", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodOnRegisteredToThrustComponent = typeof(MyThrust).GetMethod
                ("OnRegisteredToThrustComponent", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                
            var MethodFleeAwayFromTargetLogic = typeof(MyDefensiveCombatBlock).GetMethod
                ("FleeAwayFromTargetLogic", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodFlee = typeof(MyDefensiveCombatBlock).GetMethod
                ("Flee", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodFleeOnWaypointReached = typeof(MyDefensiveCombatBlock).GetMethod
                ("FleeOnWaypointReached", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodCheckIdentitiesTrash = typeof(MySessionComponentTrash).GetMethod
                ("CheckIdentitiesTrash", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var MethodMyCargoContainerInventoryBagReplicableOnSave = typeof(MyCargoContainerInventoryBagReplicable).GetMethod
                ( nameof(MyCargoContainerInventoryBagReplicable.OnSave), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            
            var finalizer = typeof(CrashFixPatch).GetMethod(nameof(SuppressExceptionFinalizer),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            
            var finalizerDispatch = typeof(CrashFixPatch).GetMethod(nameof(SuppressDispatchExceptionFinalizer),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            harmony.Patch(MethodSetDetailedInfo, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodUpdateWaypointPositions, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodNotify, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodMyExplosionsUpdateBeforeSimulation, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodMyAngleGrinderGrind, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodOnRegisteredToThrustComponent, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodRefineryDoUpdateTimerTick, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodRemoveIdentity, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodApplyDirtyGroups, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodFleeAwayFromTargetLogic, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodAssDoUpdateTimerTick, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodFlee, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodFleeOnWaypointReached, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodMyCargoContainerInventoryBagReplicableOnSave, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodCreateVoxelMap, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodAiGotoTarget, finalizer: new HarmonyMethod(finalizer));
            harmony.Patch(MethodOrchestratorDispatchBeforeSimulation, finalizer: new HarmonyMethod(finalizerDispatch));
            harmony.Patch(MethodOrchestratorDispatchAfterSimulation, finalizer: new HarmonyMethod(finalizerDispatch));
            harmony.Patch(MethodCheckIdentitiesTrash, finalizer: new HarmonyMethod(finalizerDispatch));
            harmony.Patch(MethodWolfTargetAttack, finalizer: new HarmonyMethod(finalizerDispatch));
            
            var MethodRemoveClient = typeof(MyReplicationServer).GetMethod
                ("RemoveClient", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            ctx.GetPattern(MethodRemoveClient).Prefixes.Add(
                typeof(CrashFixPatch).GetMethod(nameof(RemoveClientPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }


        public static Exception SuppressExceptionFinalizer(Exception __exception)
        {
            if (__exception != null && SentisOptimisationsPlugin.Config.EnableMainDebugLogs)
            {
                SentisOptimisationsPlugin.Log.Error(__exception, "SuppressedException ");
            }

            return null;
        }
        
        public static Exception SuppressDispatchExceptionFinalizer(Exception __exception)
        {
            if (__exception != null)
            {
                SentisOptimisationsPlugin.Log.Error(__exception, "SuppressedException ");
            }
            return null;
        }

        // A cleanup this slow (one frame) is worth a line in the log.
        private const long SlowRemoveClientMs = 16;

        /// <summary>
        /// Vanilla RemoveClient loops "while (client.Replicables.Count > 0) RemoveForClient(first)", and
        /// RemoveClientReplicable leaves a replicable that is no longer in m_replicableGroups in the
        /// client's list: the loop never ends and the server hangs on a logout. Here every replicable
        /// is removed once from a snapshot, each in its own try, and the client is dropped whatever
        /// happens - OnClientLeft repeats RemoveClient while the client is still listed.
        /// </summary>
        private static bool RemoveClientPatch(MyReplicationServer __instance, Endpoint endpoint)
        {
            IDictionary clientDataDict = null;
            try
            {
                clientDataDict = _clientStates.Invoke(__instance);
                if (!clientDataDict.Contains(endpoint))
                {
                    return false;
                }

                Object clientData = clientDataDict[endpoint];
                var clientReplicables = _replicables.Invoke(clientData);
                var sw = Stopwatch.StartNew();
                var before = clientReplicables.Count;
                foreach (var replicable in new List<IMyReplicable>(clientReplicables.Keys))
                {
                    try
                    {
                        _removeForClient.Invoke(__instance, replicable, clientData, false);
                    }
                    catch (Exception e)
                    {
                        SentisOptimisationsPlugin.Log.Error(e, "Remove For Client exception ");
                    }
                }

                var left = clientReplicables.Count;
                if (left > 0 || sw.ElapsedMilliseconds >= SlowRemoveClientMs)
                    SentisOptimisationsPlugin.Log.Warn(
                        $"Client logout cleanup: {before} replicables, {left} left behind, {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "RemoveClientPatch exception ");
            }
            finally
            {
                try
                {
                    if (clientDataDict != null && clientDataDict.Contains(endpoint))
                    {
                        clientDataDict.Remove(endpoint);
                        _recentClientsStates.Invoke(__instance)[endpoint] =
                            _callback.Invoke(__instance).GetUpdateTime() + MyTimeSpan.FromSeconds(60.0);
                    }
                }
                catch (Exception e)
                {
                    SentisOptimisationsPlugin.Log.Error(e, "RemoveClientPatch exception ");
                }
            }

            return false;
        }
        
        private static void MethodPistonInitPatched(MyPistonBase __instance)
        {
        try
        {
            __instance.Velocity.ValueChanged += VelocityOnValueChanged;
        


            }
                catch (Exception __guard_e)
                {
                    SentisOptimisationsPlugin.Log.Error("MethodPistonInitPatched exception " + __guard_e);

                }
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
                    // ConditionalCompilationSymbols property no longer exists; read the backing field.
                    var conditionalSymbols = (IEnumerable<string>) ReflectionUtils.GetInstanceField(
                        typeof(MyScriptCompiler), __instance, "m_conditionalCompilationSymbols");
                    CSharpParseOptions parseOptions = m_conditionalParseOptions.WithPreprocessorSymbols(conditionalSymbols);
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