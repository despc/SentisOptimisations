using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using Havok;
using NAPI;
using NLog;
using NLog.Filters;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Utils;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisGameplayImprovements.AllGridsActions;
using SentisOptimisations;
using SentisOptimisations.DelayedLogic;
using SentisOptimisationsPlugin.AllGridsActions;
using SentisOptimisationsPlugin.Freezer;
using SentisOptimisationsPlugin.ShipTool;
using SOPlugin.GUI;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.API.Session;
using Torch.Session;
using VRage;
using VRage.Collections;
using VRage.Library.Utils;
using VRageMath;
using VRageMath.Spatial;

namespace SentisOptimisationsPlugin
{
    public class SentisOptimisationsPlugin : TorchPluginBase, IWpfPlugin
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static TorchSessionManager SessionManager;
        private static Persistent<MainConfig> _config;
        public static MainConfig Config => _config?.Data;
        public UserControl _control = null;
        public static SentisOptimisationsPlugin Instance { get; private set; }

        public AllGridsProcessor AllGridsProcessor = new AllGridsProcessor();
        private readonly VoxelStreamCache _voxelStreamCache = new VoxelStreamCache();
        // AsyncWeld v2: the weld/grind pipeline no longer uses worker-thread queues;
        // all game-state work runs on the game thread (see ShipToolPatch / WelderOptimization).
        public DelayedProcessor DelayedProcessor = new DelayedProcessor();
        public static ShieldApi SApi = new ShieldApi();

        public override void Init(ITorchBase torch)
        {
            Instance = this;
            DelayedProcessor.Instance = DelayedProcessor;
            Log.Info("Init SentisOptimisationsPlugin");
            // first, before the plugins' patches are committed: Torch's patch jumps one instruction each
            CrashFix.TorchJumpFix.Install();
            // also before them: the fix goes into the same commits, see ReemitLeaveFix
            CrashFix.ReemitLeaveFix.Install(torch.Managers.GetManager<Torch.Managers.PatchManager.PatchManager>());
            MyFakes.ENABLE_SCRAP = false;
            MySimpleProfiler.ENABLE_SIMPLE_PROFILER = false;
            // Full collections in the background, not stopping the game thread: with the default mode a blocking
            // gen2 of a 1-4 GB heap every minute or two was a frame of 100-200 ms (SentisWatcher's perf table). Blocking
            // ones still happen when memory runs short.
            var latency = System.Runtime.GCSettings.LatencyMode;
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
            if (global::SentisOptimisations.DiagLog.On) Log.Info($"GC latency mode {latency} -> {System.Runtime.GCSettings.LatencyMode}, server GC {System.Runtime.GCSettings.IsServerGC}");

            SetupConfig();
            SessionManager = Torch.Managers.GetManager<TorchSessionManager>();
            if (SessionManager == null)
                return;

            MyEntities.OnEntityAdd += EntitiesObserver.MyEntitiesOnOnEntityAdd;
            MyEntities.OnEntityRemove += EntitiesObserver.MyEntitiesOnOnEntityRemove;
            SessionManager.SessionStateChanged += SessionManager_SessionStateChanged;
            ReflectionUtils.SetPrivateStaticField(typeof(MyCubeBlockDefinition),
                nameof(MyCubeBlockDefinition.PCU_CONSTRUCTION_STAGE_COST), 0);

            var stringCondition = "contains('${message}','Invalid triangle')";
            var stringCondition2 = "contains('${message}','Trying to remove entity with name')";
            var stringCondition3 = "contains('${message}','Sound on different thread')";
            
            foreach (var rule in LogManager.Configuration.LoggingRules)
            {
               rule.DisableLoggingForLevel(LogLevel.Debug);
               rule.Filters.Add(new ConditionBasedFilter()
               {
                   Condition = stringCondition,
                   Action = FilterResult.Ignore
               });
               rule.Filters.Add(new ConditionBasedFilter()
               {
                   Condition = stringCondition2,
                   Action = FilterResult.Ignore
               });
               rule.Filters.Add(new ConditionBasedFilter()
               {
                   Condition = stringCondition3,
                   Action = FilterResult.Ignore
               });
            }
            LogManager.ReconfigExistingLoggers();
        }


        private void SessionManager_SessionStateChanged(
            ITorchSession session,
            TorchSessionState newState)
        {
            if (newState == TorchSessionState.Unloading)
            {
                UnloadShieldApi();
                AllGridsProcessor.OnUnloading();
                Optimizer.Optimizations.GrinderPatches.ClearAll();
                GasTankOptimisations.ClearAll();
                _voxelStreamCache.OnUnloading();
                SerializerWarmup.Reset();
                ReplicablesPatch.ClearAll();
                Freezer.WakeRequests.ClearAll();
                Freezer.FrozenProduction.ClearAll();
                DelayedProcessor.OnUnloading();
                MyEntities.OnEntityAdd -= EntitiesObserver.MyEntitiesOnOnEntityAdd;
                MyEntities.OnEntityRemove -= EntitiesObserver.MyEntitiesOnOnEntityRemove;
                EntitiesObserver.ClearAll();
            }
            else
            {
                if (newState != TorchSessionState.Loaded)
                    return;
                Optimizer.Optimizations.PhysicsLoadMonitor.Reset();
                SerializerWarmup.Run();
                PluginJitWarmup.Run();
                AllGridsProcessor.OnLoaded();
                _voxelStreamCache.OnLoaded();
                DelayedProcessor.OnLoaded();
                // re-subscribe (idempotent) for worlds loaded after Init, then seed the cache
                MyEntities.OnEntityAdd -= EntitiesObserver.MyEntitiesOnOnEntityAdd;
                MyEntities.OnEntityAdd += EntitiesObserver.MyEntitiesOnOnEntityAdd;
                MyEntities.OnEntityRemove -= EntitiesObserver.MyEntitiesOnOnEntityRemove;
                MyEntities.OnEntityRemove += EntitiesObserver.MyEntitiesOnOnEntityRemove;
                EntitiesObserver.PrimeFromAllEntities();
                InitShieldApi();
            }
        }

        private System.Threading.CancellationTokenSource _shieldApiCts;

        public async void InitShieldApi()
        {
            _shieldApiCts?.Cancel();
            var cts = _shieldApiCts = new System.Threading.CancellationTokenSource();
            try
            {
                await Task.Delay(60000, cts.Token);
                // the delay resumes on a pool thread, and the session may have been unloaded meanwhile:
                // ModAPI is touched only on the game thread and only while the session is alive
                MySandboxGame.Static?.Invoke(() =>
                {
                    if (cts.IsCancellationRequested || MySession.Static == null || MyAPIGateway.Utilities == null)
                        return;
                    try
                    {
                        SApi.Load();
                    }
                    catch (Exception e)
                    {
                        Log.Error(e);
                    }
                }, "SentisShieldApiLoad");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private void UnloadShieldApi()
        {
            _shieldApiCts?.Cancel();
            _shieldApiCts = null;
            try
            {
                if (MyAPIGateway.Utilities != null)
                    SApi.Unload();
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private static string _lastCompensationSummary;

        /// <summary>
        /// What the freezer has handed back to thawed grids, for the compensation log - not the
        /// panel. Written only when it changed, so an idle server does not repeat it.
        /// </summary>
        private static void LogCompensationSummary()
        {
            try
            {
                if (!Config.EnableCompensationLogs) return;
                var summary = Freezer.FrozenProduction.Summary();
                if (summary == _lastCompensationSummary) return;
                _lastCompensationSummary = summary;
                FreezeLogic.CompensationLogs("Freezer " + summary);
            }
            catch (Exception e)
            {
                Log.Error(e, "compensation summary failed");
            }
        }

        public void UpdateGui()
        {
            try
            {
                ListReader<MyClusterTree.MyCluster> clusters = MyPhysics.Clusters.GetClusters();
                var myPhysics = MySession.Static.GetComponent<MyPhysics>();
                int active = 0;
                foreach (MyClusterTree.MyCluster myCluster in new List<MyClusterTree.MyCluster>(clusters))
                {
                    if (myCluster.UserData is HkWorld userData && (bool) myPhysics.easyCallMethod("IsClusterActive",
                            new object[] {myCluster.ClusterId, userData.CharacterRigidBodies.Count}))
                    {
                        active++;
                    }
                }

                var clustersCount = clusters.Count;
                LogCompensationSummary();

                Instance.UpdateUI(x =>
                {
                    var gui = x as ConfigGUI;
                    gui.ClustersStatistic.Text =
                        $"Count: {clustersCount}, Active: {active}";
                    try
                    {
                        gui.FreezerStatistic.Text =
                            $"Avg CPU Load: {FreezeLogic.GetAvgCpuLoad()}%, Peak {Freezer.CpuLoadPeak.Seconds}s: {Freezer.CpuLoadPeak.Peak()}%, " +
                            $"Physics: {Optimizer.Optimizations.PhysicsLoadMonitor.AverageMs:F2} ms/frame (last {Optimizer.Optimizations.PhysicsLoadMonitor.LastMs:F2}) " +
                            $"Total grids: {EntitiesObserver.MyCubeGrids.Count}, Frozen: {FreezeLogic.FrozenGrids.Count}, Frozen physics: {FreezeLogic.FrozenPhysicsGrids.Count}";
                    }
                    catch (Exception e)
                    {
                       //do nothing
                    }

                    try
                    {
                        gui.ScriptsExpander.Header =
                            $"Scripts: {PbLoad.Running()} running, {PbLoad.TotalMsPerFrame():F2} ms/frame in total";
                        gui.ScriptsStatistic.Text = PbLoad.Report(20);
                    }
                    catch (Exception e)
                    {
                        //do nothing
                    }
                });
            }
            catch (Exception e)
            {
                //do nothing
            }
        }

        public void UpdateUI(Action<UserControl> action)
        {
            try
            {
                if (_control != null)
                {
                    _control.Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            action.Invoke(_control);
                        }
                        catch (Exception e)
                        {
                            Log.Error(e, "Something wrong in executing function:" + action);
                        }
                    });
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Cant UpdateUI");
            }
        }

        public override void Update()
        {
            Optimizer.Optimizations.GameThreadCores.Apply();
            Optimizer.Optimizations.GrinderPatches.Flush();
            Freezer.CpuLoadPeak.Sample(MySandboxGame.Static.CPULoad);
            Optimizer.Optimizations.WelderOptimization.RunDeferred();
            Freezer.FrozenProduction.Tick();
            Optimizer.Optimizations.SafeZoneGridTracking.Tick();
            RespawnPointsCache.Tick();
            FaunaSpawnPrefetch.Tick();
        }

        public UserControl GetControl()
        {
            if (_control == null)
            {
                _control = new ConfigGUI();
            }

            return _control;
        }

        private void SetupConfig()
        {
            _config = Persistent<MainConfig>.Load(Path.Combine(StoragePath, "SentisOptimisations.cfg"));
        }

        public static void SaveConfig()
        {
            _config?.Save();
        }

        public override void Dispose()
        {
            _config.Save(Path.Combine(StoragePath, "SentisOptimisations.cfg"));
            base.Dispose();
        }
    }
}