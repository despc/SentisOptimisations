using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using Havok;
using NAPI;
using NLog;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Utils;
using Sandbox.Engine.Voxels;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisation.PveZone;
using SentisOptimisations;
using SentisOptimisationsPlugin.AllGridsActions;
using SOPlugin.GUI;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.API.Session;
using Torch.Commands;
using Torch.Commands.Permissions;
using Torch.Session;
using VRage;
using VRage.Collections;
using VRage.Game.ModAPI;
using VRage.Game.Voxels;
using VRage.Network;
using VRageMath;
using VRageMath.Spatial;

namespace SentisOptimisationsPlugin
{
    public class SentisOptimisationsPlugin : TorchPluginBase, IWpfPlugin
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static PcuLimiter _limiter = new PcuLimiter();
        public static Dictionary<long,long> stuckGrids = new Dictionary<long, long>();
        public static Dictionary<long,long> gridsInSZ = new Dictionary<long, long>();
        private static TorchSessionManager SessionManager;
        private static Persistent<MainConfig> _config;
        public static MainConfig Config => _config.Data;
        public static Random _random = new Random();
        public UserControl _control = null;
        public static SentisOptimisationsPlugin Instance { get; private set; }

        private AllGridsObserver _allGridsObserver = new AllGridsObserver();
        public static ShieldApi SApi = new ShieldApi();

        public override void Init(ITorchBase torch)
        {
            Instance = this;
            Log.Info("Init SentisOptimisationsPlugin");
            MyFakes.ENABLE_SCRAP = false;
            MySimpleProfiler.ENABLE_SIMPLE_PROFILER = false;
            SetupConfig();
            SessionManager = Torch.Managers.GetManager<TorchSessionManager>();
            if (SessionManager == null)
                return;
            MyEntities.OnEntityAdd += _allGridsObserver.MyEntitiesOnOnEntityAdd;
            MyEntities.OnEntityRemove += _allGridsObserver.MyEntitiesOnOnEntityRemove;
            var configOverrideModIds = Config.OverrideModIds;
            SessionManager.SessionStateChanged += SessionManager_SessionStateChanged;
            if (string.IsNullOrEmpty(configOverrideModIds))
            {
                foreach (var modId in configOverrideModIds.Split(','))
                {
                    if (string.IsNullOrEmpty(configOverrideModIds))
                    {
                        try
                        {
                            var modIdL = Convert.ToUInt64(modId);
                            SessionManager.AddOverrideMod(modIdL);
                        }
                        catch (Exception e)
                        {
                            Log.Warn("Skip wrong modId " + modId);
                        }
                    }
                }
            }
        }



        private void SessionManager_SessionStateChanged(
            ITorchSession session,
            TorchSessionState newState)
        {
            if (newState == TorchSessionState.Unloading)
            {
                _limiter.OnUnloading();
                _allGridsObserver.OnUnloading();
            }
            else
            {
                if (newState != TorchSessionState.Loaded)
                    return;
                DamagePatch.Init();
                _limiter.OnLoaded();
                _allGridsObserver.OnLoaded();
                InitShieldApi();
                Communication.RegisterHandlers();
                PvECore.Init();
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
                    if (myCluster.UserData is HkWorld userData && (bool)myPhysics.easyCallMethod("IsClusterActive", new object[]{myCluster.ClusterId, userData.CharacterRigidBodies.Count}))
                    {
                        active++;
                    }
                }
                
                var clustersCount = clusters.Count;
                
                Instance.UpdateUI((x) =>
                {
                    var gui = x as ConfigGUI;

                    gui.ClustersStatistic.Text =
                        $"Count: {clustersCount}, Active: {active}";
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
            if (MySandboxGame.Static.SimulationFrameCounter % 500 == 0)
            {
                Task.Run(UpdateGui);
                foreach (var keyValuePair in new Dictionary<long, long>(SafezonePatch.entitiesInSZ))
                {
                    var entityId = keyValuePair.Key;
                    var entityById = MyEntities.GetEntityById(entityId);
                    var displayName = "";
                    if (entityById != null)
                    {
                        displayName = entityById.DisplayName;  
                    }

                    var time = keyValuePair.Value;
                    if (time > 10)
                    {
                        Log.Error("Entity in sz " + entityId + "   " + displayName + " time - " + time);
                        if (gridsInSZ.ContainsKey(entityId))
                        {
                            if (gridsInSZ[entityId] > 3)
                            {
                                if (entityById is MyCubeGrid)
                                {
                                    try
                                    {
                                        var myCubeGrid = ((MyCubeGrid)entityById);
                                        if (!myCubeGrid.IsStatic)
                                        {
                                            myCubeGrid.Physics?.SetSpeeds(Vector3.Zero, Vector3.Zero);
                                            myCubeGrid.ConvertToStatic();
                                            try
                                            {
                                                MyMultiplayer.RaiseEvent<MyCubeGrid>(myCubeGrid, (MyCubeGrid x) => new Action(x.ConvertToStatic), default(EndpointId));
                                                foreach (var player in MySession.Static.Players.GetOnlinePlayers())
                                                {
                                                    MyMultiplayer.RaiseEvent<MyCubeGrid>(myCubeGrid, (MyCubeGrid x) => new Action(x.ConvertToStatic), new EndpointId(player.Id.SteamId));
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                Log.Error(ex, "()Exception in RaiseEvent.");
                                            }
                                            if (myCubeGrid.BigOwners.Count > 0)
                                            {
                                           
                                                ChatUtils.SendTo(myCubeGrid.BigOwners[0], "Структура " + displayName + " конвертирована в статику в связи с дудосом");
                                                MyVisualScriptLogicProvider.ShowNotification("Структура " + displayName + " конвертирована в статику в связи с дудосом", 10000,
                                                    "Red",
                                                    myCubeGrid.BigOwners[0]);  
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
                                    
                                }
                                Log.Error("Anything else fck sz " + entityId + "   " + displayName + " time - " + time);
                                gridsInSZ[entityId] = 0;
                                continue;
                            }
                            gridsInSZ[entityId] = gridsInSZ[entityId] + 1; 
                            
                        }
                        else
                        {
                            gridsInSZ[entityId] = 1;
                        }
                    }
                }
                SafezonePatch.entitiesInSZ.Clear();
            }

            if (MySandboxGame.Static.SimulationFrameCounter % 120 == 0)
            {
                foreach (var keyValuePair in DamagePatch.contactInfo)
                {
                    var entityId = keyValuePair.Key;
                    var entityById = MyEntities.GetEntityById(entityId);
                    if (!(entityById is MyCubeGrid))
                    {
                        continue;
                    }

                    var contactCount = keyValuePair.Value;
                    if (contactCount > 50)
                    {
                        Log.Error("Entity  " + entityById.DisplayName + " position " +
                                  entityById.PositionComp.GetPosition() + " contact count - " + contactCount);
                    }

                    if (contactCount < 800)
                    {
                        continue;
                    }

                    if (stuckGrids.ContainsKey(entityId))
                    {
                        if (stuckGrids[entityId] > 5)
                        {
                            var myCubeGrid = ((MyCubeGrid) entityById);
                            if (!Vector3.IsZero(
                                MyGravityProviderSystem.CalculateNaturalGravityInPoint(entityById.WorldMatrix
                                    .Translation)))
                            {
                                myCubeGrid.Physics?.SetSpeeds(Vector3.Zero, Vector3.Zero);
                                myCubeGrid.ConvertToStatic();
                                try
                                {
                                    MyMultiplayer.RaiseEvent<MyCubeGrid>(myCubeGrid, (MyCubeGrid x) => new Action(x.ConvertToStatic), default(EndpointId));
                                    foreach (var player in MySession.Static.Players.GetOnlinePlayers())
                                    {
                                        MyMultiplayer.RaiseEvent<MyCubeGrid>(myCubeGrid, (MyCubeGrid x) => new Action(x.ConvertToStatic), new EndpointId(player.Id.SteamId));
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, "()Exception in RaiseEvent.");
                                }
                            }
                            else
                            {
                                try
                                {
                                    Log.Info("Teleport stuck grid " + myCubeGrid.DisplayName);
                                    MatrixD worldMatrix = myCubeGrid.WorldMatrix;
                                    var position = myCubeGrid.PositionComp.GetPosition();

                                    var garbageLocation = new Vector3D(position.X + _random.Next(-10000, 10000),
                                        position.Y + _random.Next(-10000, 10000),
                                        position.Z + _random.Next(-10000, 10000));
                                    worldMatrix.Translation = garbageLocation;
                                    myCubeGrid.Teleport(worldMatrix, (object) null, false);
                                }
                                catch (Exception e)
                                {
                                    Log.Error("Exception in time try teleport entity to garbage", e);
                                }
                            }

                            stuckGrids.Remove(entityId);
                            continue;
                        }

                        stuckGrids[entityId] = stuckGrids[entityId] + 1;
                        continue;
                    }
                    stuckGrids[entityId] = 1;
                }

                DamagePatch.contactInfo.Clear();
            }
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

        public class TestCommands : CommandModule
        {
            [Command("fix-asters", "Fix astersCommand")]
            [Permission(MyPromoteLevel.Moderator)]
            public void FixAsters()
            {
                var myVoxelMaps = MyEntities.GetEntities().OfType<IMyVoxelMap>().ToArray<IMyVoxelMap>();
                for (int i = 0; i < myVoxelMaps.Count(); i++)
                {
                    try
                    {
                        
                        var voxelMap = myVoxelMaps[i];
                        if (voxelMap.Name == null)
                        {
                            continue;
                        }
                        if (!voxelMap.Name.Contains("Field"))
                        {
                            continue;
                        }
                        Vector3D position = voxelMap.GetPosition();
                        byte[] storageData;
                        voxelMap.Storage.Save(out storageData);
                        IMyStorage storage = MyAPIGateway.Session.VoxelMaps.CreateStorage(storageData) as IMyStorage;
                        voxelMap.PositionComp.SetPosition(Vector3D.Zero);
                        var addVoxelMap = MyWorldGenerator.AddVoxelMap(voxelMap.Name + "new", (MyStorageBase) storage, position);
                        addVoxelMap.PositionComp.SetPosition(position);
                        Log.Error("refresh voxels " + voxelMap.DisplayName);
                    }
                    catch (Exception e)
                    {
                        Log.Error("Exception ", e);
                    }
                }
            }
        }
        public override void Dispose()
        {
            _config.Save(Path.Combine(StoragePath, "SentisOptimisations.cfg"));
            _allGridsObserver.CancellationTokenSource.Cancel();
            base.Dispose();
        }
    }
}