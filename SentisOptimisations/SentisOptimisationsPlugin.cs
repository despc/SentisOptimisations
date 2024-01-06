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
using Optimizer.Optimizations;
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
using VRage.Network;
using VRageMath;
using VRageMath.Spatial;

namespace SentisOptimisationsPlugin
{
    public class SentisOptimisationsPlugin : TorchPluginBase, IWpfPlugin
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static Dictionary<long, long> gridsInSZ = new Dictionary<long, long>();
        private static TorchSessionManager SessionManager;
        private static Persistent<MainConfig> _config;
        public static MainConfig Config => _config.Data;
        public UserControl _control = null;
        public static SentisOptimisationsPlugin Instance { get; private set; }

        private AllGridsProcessor _allGridsProcessor = new AllGridsProcessor();
        private SendReplicablesAsync _replicablesAsync = new SendReplicablesAsync();
        public ShipToolsAsyncQueues ShipToolsAsyncQueues = new ShipToolsAsyncQueues();
        public DelayedProcessor DelayedProcessor = new DelayedProcessor();
        public static ShieldApi SApi = new ShieldApi();

        public override void Init(ITorchBase torch)
        {
            Instance = this;
            DelayedProcessor.Instance = DelayedProcessor;
            Log.Info("Init SentisOptimisationsPlugin");
            MyFakes.ENABLE_SCRAP = false;
            MySimpleProfiler.ENABLE_SIMPLE_PROFILER = false;
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
            }
            LogManager.ReconfigExistingLoggers();
        }


        private void SessionManager_SessionStateChanged(
            ITorchSession session,
            TorchSessionState newState)
        {
            if (newState == TorchSessionState.Unloading)
            {
                _allGridsProcessor.OnUnloading();
                _replicablesAsync.OnUnloading();
                ShipToolsAsyncQueues.OnUnloading();
                DelayedProcessor.OnUnloading();
            }
            else
            {
                if (newState != TorchSessionState.Loaded)
                    return;
                _allGridsProcessor.OnLoaded();
                _replicablesAsync.OnLoaded();
                ShipToolsAsyncQueues.OnLoaded();
                DelayedProcessor.OnLoaded();
                InitShieldApi();
                WelderOptimization.AsyncWeldLoopInit();
            }
        }

        public async void InitShieldApi()
        {
            try
            {
                await Task.Delay(60000);
                SApi.Load();
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        public void UpdateGui()
        {
            try
            {
                ListReader<MyClusterTree.MyCluster> clusters = MyPhysics.Clusters.GetClusters();
                var myPhysics = MySession.Static.GetComponent<MyPhysics>();
                int active = 0;
                foreach (MyClusterTree.MyCluster myCluster in clusters)
                {
                    if (myCluster.UserData is HkWorld userData && (bool) myPhysics.easyCallMethod("IsClusterActive",
                            new object[] {myCluster.ClusterId, userData.CharacterRigidBodies.Count}))
                    {
                        active++;
                    }
                }

                var clustersCount = clusters.Count;

                Instance.UpdateUI(x =>
                {
                    var gui = x as ConfigGUI;
                    gui.ClustersStatistic.Text =
                        $"Count: {clustersCount}, Active: {active}";
                    try
                    {
                        var cpuLoads = new List<float> (FreezeLogic.CpuLoads);
                        var avgCpuLoad = cpuLoads.Count > 0 ? cpuLoads.Average() : 0.0;
                        gui.FreezerStatistic.Text =
                            $"Avg CPU Load: {Math.Round((decimal)avgCpuLoad, 2)}% " +
                            $"Total grids: {EntitiesObserver.MyCubeGrids.Count}, Frozen: {FreezeLogic.FrozenGrids.Count}";
                    }
                    catch (Exception e)
                    {
                       //do nothing
                    }
                    
                });
            }
            catch (Exception e)
            {
                Log.Error(e, "WTF?");
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
            FrameExecutor.Update();
            if (MySandboxGame.Static.SimulationFrameCounter % 600 == 0)
            {
                DelayedProcessor.Instance.AddDelayedAction(DateTime.Now, DetectSZDDos);
            }
            if (MySandboxGame.Static.SimulationFrameCounter % 300 == 0)
            {
                DelayedProcessor.Instance.AddDelayedAction(DateTime.Now, UpdateGui);
            }
        }

        private static void DetectSZDDos()
        {
            foreach (var keyValuePair in new Dictionary<long, GridInSzInfo>(SafezonePatch.EntitiesInSZ))
            {
                var entityId = keyValuePair.Key;
                var cubeGrid = keyValuePair.Value.MyCubeGrid;
                var displayName = "";
                if (cubeGrid != null)
                {
                    displayName = cubeGrid.DisplayName;
                }

                var time = keyValuePair.Value.DDosTimeMs;
                if (time > 5)
                {
                    Log.Error("Entity in sz " + entityId + "   " + displayName + " time - " + time);
                    if (gridsInSZ.ContainsKey(entityId))
                    {
                        if (gridsInSZ[entityId] > 1)
                        {
                            try
                            {
                                if (!cubeGrid.IsStatic)
                                {
                                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                                    {
                                        cubeGrid.Physics?.SetSpeeds(Vector3.Zero, Vector3.Zero);
                                        cubeGrid.ConvertToStatic();
                                    });
                                    CommunicationUtils.SyncConvert(cubeGrid, true);
                                    try
                                    {
                                        MyMultiplayer.RaiseEvent(cubeGrid,
                                            x => x.ConvertToStatic, default);
                                        foreach (var player in MySession.Static.Players.GetOnlinePlayers())
                                        {
                                            MyMultiplayer.RaiseEvent(cubeGrid,
                                                x => x.ConvertToStatic,
                                                new EndpointId(player.Id.SteamId));
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Log.Error(ex, "()Exception in RaiseEvent.");
                                    }

                                    if (cubeGrid.BigOwners.Count > 0)
                                    {
                                        ChatUtils.SendTo(cubeGrid.BigOwners[0],
                                            "Структура " + displayName + " конвертирована в статику в связи с дудосом");
                                        MyVisualScriptLogicProvider.ShowNotification(
                                            "Структура " + displayName + " конвертирована в статику в связи с дудосом",
                                            10000,
                                            "Red",
                                            cubeGrid.BigOwners[0]);
                                    }

                                    Log.Error("Grid " + displayName + " Converted To Static");
                                    gridsInSZ[entityId] = 0;
                                    continue;
                                }
                            }
                            catch (Exception e)
                            {
                                Log.Error(e);
                            }

                            gridsInSZ[entityId] = 0;
                            continue;
                        }

                        gridsInSZ[entityId] += 1;
                    }
                    else
                    {
                        gridsInSZ[entityId] = 1;
                    }
                }
            }

            SafezonePatch.EntitiesInSZ.Clear();
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

        public override void Dispose()
        {
            _config.Save(Path.Combine(StoragePath, "SentisOptimisations.cfg"));
            _replicablesAsync.CancellationTokenSource.Cancel();
            base.Dispose();
        }
    }
}