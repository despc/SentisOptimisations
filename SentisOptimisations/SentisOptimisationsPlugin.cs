using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Controls;
using HarmonyLib;
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
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations;
using SentisOptimisationsPlugin.AllGridsActions;
using SentisOptimisationsPlugin.ShipTool;
using SOPlugin.GUI;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.API.Session;
using Torch.Commands;
using Torch.Commands.Permissions;
using Torch.Managers;
using Torch.Session;
using VRage.Collections;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.Voxels;
using VRage.Network;
using VRage.ObjectBuilders;
using VRageMath;
using VRageMath.Spatial;

namespace SentisOptimisationsPlugin
{
    public class SentisOptimisationsPlugin : TorchPluginBase, IWpfPlugin
    {
        private static Guid NexusGUID = new Guid("28a12184-0422-43ba-a6e6-2e228611cca5");
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static PcuLimiter _limiter = new PcuLimiter();
        public static Dictionary<long,long> stuckGrids = new Dictionary<long, long>();
        public static Dictionary<long,long> gridsInSZ = new Dictionary<long, long>();
        private static TorchSessionManager SessionManager;
        private static Persistent<MainConfig> _config;
        public static Harmony harmony = new Harmony("SentisOptimisations.H");
        public static MainConfig Config => _config.Data;
        public static Random _random = new Random();
        public UserControl _control = null;
        public static SentisOptimisationsPlugin Instance { get; private set; }

        private FuckWelderProcessor _welderProcessor = new FuckWelderProcessor();
        private AllGridsObserver _allGridsObserver = new AllGridsObserver();
        public static ShieldApi SApi = new ShieldApi();


        static void MyHandler(object sender, UnhandledExceptionEventArgs args)
        {
            Exception e = (Exception) args.ExceptionObject;
            Log.Error("MyHandler caught : " + e.Message);
            Log.Error(e);
            Log.Error("Runtime terminating: {0}", args.IsTerminating);
        }
        public override void Init(ITorchBase torch)
        {
            AppDomain currentDomain = AppDomain.CurrentDomain;
            currentDomain.UnhandledException += new UnhandledExceptionEventHandler(MyHandler);
            
            Instance = this;
            Log.Info("Init SentisOptimisationsPlugin");
            MyFakes.ENABLE_SCRAP = false;
            SetupConfig();
            PerfomancePatch.Patch();
            GrindPaintFix.Patch();
            SessionManager = Torch.Managers.GetManager<TorchSessionManager>();
            if (SessionManager == null)
                return;
                    //SessionManager.AddOverrideMod(2891865838);
            SessionManager.SessionStateChanged += SessionManager_SessionStateChanged;
            // MyClusterTree.IdealClusterSize = new Vector3(Config.IdealClusterSize);
            // MyClusterTree.IdealClusterSizeHalfSqr =
            //     MyClusterTree.IdealClusterSize * MyClusterTree.IdealClusterSize / 4f;
            // MyClusterTree.MinimumDistanceFromBorder = MyClusterTree.IdealClusterSize / 100f;
            // MyClusterTree.MaximumForSplit = MyClusterTree.IdealClusterSize * 2f;
            // MyClusterTree.MaximumClusterSize = Config.MaximumClusterSize;
        }

        private void SessionManager_SessionStateChanged(
            ITorchSession session,
            TorchSessionState newState)
        {
            if (newState == TorchSessionState.Unloading)
            {
                _limiter.OnUnloading();
                _allGridsObserver.OnUnloading();
                ConveyorPatch.OnUnloading();
            }
            else
            {
                if (newState != TorchSessionState.Loaded)
                    return;
                DamagePatch.Init();
                _limiter.OnLoaded();
                _allGridsObserver.OnLoaded();
                InitShieldApi();
                ConveyorPatch.OnLoaded();
                Communication.RegisterHandlers();
                ITorchPlugin Plugin;
                if (DependencyProviderExtensions
                    .GetManager<PluginManager>((IDependencyProvider) this.Torch.CurrentSession.Managers).Plugins
                    .TryGetValue(NexusGUID, out Plugin))
                {
                    AquireNexus(Plugin);
                }
                    
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

        private static void AquireNexus(ITorchPlugin Plugin)
        {
            Type type = ((object) Plugin).GetType()?.Assembly.GetType("Nexus.API.PluginAPISync");
            if (type == (Type) null)
                return;
            type.GetMethod("ApplyPatching", BindingFlags.Static | BindingFlags.NonPublic).Invoke((object) null, new object[2]
            {
                (object) typeof (NexusAPI),
                (object) "SentisOptimisations"
            });
            NexusSupport.Init();
        }
        
        public void UpdateGui()
        {
            try
            {

                
                var conveyourCache = ConveyorPatch.ConveyourCache;
                var cachedGrids = conveyourCache.Count;
                var totalCacheCount = 0;
                var uncachedCalls = ConveyorPatch.UncachedCalls;
                foreach (var keyValuePair in conveyourCache)
                {
                    totalCacheCount = totalCacheCount + keyValuePair.Value.Count;
                }

                ListReader<MyClusterTree.MyCluster> clusters = MyPhysics.Clusters.GetClusters();
                var myPhysics = MySession.Static.GetComponent<MyPhysics>();
                int active = 0;
                foreach (MyClusterTree.MyCluster myCluster in clusters)
                {

                    if (SentisOptimisationsPlugin.Config.PatchClusterActivity)
                    {
                        if (ClusterActivityCheck.ActiveClusters.Contains(myCluster.ClusterId))
                        {
                            active++;
                        }

                        continue;
                    }
                    if (myCluster.UserData is HkWorld userData && (bool)myPhysics.easyCallMethod("IsClusterActive", new object[]{myCluster.ClusterId, userData.CharacterRigidBodies.Count}))
                    {
                        active++;
                    }
                }
                
                var clustersCount = clusters.Count;
                
                Instance.UpdateUI((x) =>
                {
                    var gui = x as ConfigGUI;

                    gui.CacheStatistic.Text =
                        $"Cached grids: {cachedGrids} ||  Total cache size: {totalCacheCount} ||  Uncached calls: {uncachedCalls}";
                    gui.ClustersStatistic.Text =
                        $"Count: {clustersCount}, Active: {active}";
                });
                ConveyorPatch.UncachedCalls = 0;
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

            if (MySandboxGame.Static.SimulationFrameCounter % 60 == 0)
            {
                _welderProcessor.Process();
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
                    if (contactCount > Config.ContactCountAlert)
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
            if (MySandboxGame.Static.SimulationFrameCounter % 6000 == 0)
            {
                foreach (KeyValuePair<long, MyFaction> faction in MySession.Static.Factions)
                {
                    foreach (MyStation station in faction.Value.Stations)
                    {
                        if (station.StationEntityId != 0L &&
                            MyEntities.GetEntityById(station.StationEntityId) is MyCubeGrid entityById)
                        {
                            foreach (var mySlimBlock in entityById.GetBlocks())
                            {
                                if (mySlimBlock.FatBlock is MyBatteryBlock block)
                                {
                                    var myBatteryBlock = block;
                                    myBatteryBlock.CurrentStoredPower = myBatteryBlock.MaxStoredPower;
                                }
                                if (mySlimBlock.FatBlock is MyReactor reactor)
                                {
                                    var myInventory = reactor.GetInventory();
                                    var definitionId = new MyDefinitionId(typeof(MyObjectBuilder_Ingot), "Uranium");
                                    var content = (MyObjectBuilder_PhysicalObject) MyObjectBuilderSerializer.CreateNewObject(definitionId);
                                    MyObjectBuilder_InventoryItem inventoryItem = new MyObjectBuilder_InventoryItem
                                        {Amount = 1000, Content = content};
                                    myInventory.AddItems(100, inventoryItem);
                                }
                                if (mySlimBlock.FatBlock is MyGasTank tank)
                                {
                                    tank.ChangeFillRatioAmount(1);
                                }
                            }
                        }
                    }
                }
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
                        Vector3D position = voxelMap.GetPosition();
                        byte[] storageData;
                        voxelMap.Storage.Save(out storageData);
                        IMyStorage storage = MyAPIGateway.Session.VoxelMaps.CreateStorage(storageData) as IMyStorage;
                        voxelMap.Close();
                        var addVoxelMap = MyWorldGenerator.AddVoxelMap(voxelMap.Name, (MyStorageBase) storage, position);
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