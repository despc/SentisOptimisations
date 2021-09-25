using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FixTurrets.Garage;
using NLog;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Utils;
using Sandbox.Engine.Voxels;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations;
using SentisOptimisationsPlugin.Clusters;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Session;
using Torch.Commands;
using Torch.Commands.Permissions;
using Torch.Session;
using VRage.Game.ModAPI;
using VRage.Game.Voxels;
using VRage.Network;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    public class SentisOptimisationsPlugin : TorchPluginBase
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static PcuLimiter _limiter = new PcuLimiter();
        public static OldGridProcessor _oldGridProcessor = new OldGridProcessor();
        public static ClusterBuilder _cb = new ClusterBuilder();
        public static Dictionary<long,long> stuckGrids = new Dictionary<long, long>();
        public static Dictionary<long,long> gridsInSZ = new Dictionary<long, long>();
        public static MethodInfo m_myProgrammableBlockKillProgramm;
        private static TorchSessionManager SessionManager;
        public static Config Config;
        public static Random _random = new Random();
        public static SentisOptimisationsPlugin Instance { get; private set; }

        public override void Init(ITorchBase torch)
        {
            Instance = this;
            Log.Info("Init SentisOptimisationsPlugin");
            MyFakes.ENABLE_SCRAP = false;
            SetupConfig();
            SessionManager = Torch.Managers.GetManager<TorchSessionManager>();
            if (SessionManager == null)
                return;
            SessionManager.SessionStateChanged += SessionManager_SessionStateChanged;
            m_myProgrammableBlockKillProgramm = typeof (MyProgrammableBlock).GetMethod("OnProgramTermination", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        private void SessionManager_SessionStateChanged(
            ITorchSession session,
            TorchSessionState newState)
        {
            if (newState == TorchSessionState.Unloading)
            {
                _limiter.OnUnloading();
                _cb.OnUnloading();
            }
            else
            {
                if (newState != TorchSessionState.Loaded)
                    return;
                DamagePatch.Init();
                _limiter.OnLoaded();
                _cb.OnLoaded();
                _oldGridProcessor.OnLoaded();
                Communication.RegisterHandlers();
            }
        }

        public override void Update()
        {

            if (MySandboxGame.Static.SimulationFrameCounter % 500 == 0)
            {
                
                foreach (var keyValuePair in new Dictionary<long, long>(PerfomancePatch.entityesInSZ))
                {
                    var entityId = keyValuePair.Key;
                    var entityById = MyEntities.GetEntityById(entityId);
                    var displayName = "";
                    if (entityById != null)
                    {
                        displayName = entityById.DisplayName;  
                    }

                    var time = keyValuePair.Value;
                    if (time > 5)
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
                PerfomancePatch.entityesInSZ.Clear();
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
                    if (contactCount > 150)
                    {
                        Log.Error("Entity  " + entityById.DisplayName + " position " +
                                  entityById.PositionComp.GetPosition() + " contact count - " + contactCount);
                    }

                    if (contactCount < 2000)
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

                                    var garbageLocation = new Vector3D(position.X + _random.Next(-100000, 100000),
                                        position.Y + _random.Next(-100000, 100000),
                                        position.Z + _random.Next(-100000, 100000));
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
                            }
                        }
                    }
                }
            }
        }

        private void SetupConfig()
        {
            
            Config = ConfigUtils.Load<Config>( this, "SentisOptimisations.cfg");
            ConfigUtils.Save( this, Config, "SentisOptimisations.cfg");

            // try
            // {
            //     _config = Persistent<Config>.Load(configFile);
            // }
            // catch (Exception e)
            // {
            //     Log.Warn(e);
            // }
            //
            // if (_config?.Data == null)
            // {
            //     Log.Info("Create Default Config, because none was found!");
            //
            //     _config = new Persistent<Config>(configFile, new Config());
            //     _config.Save();
            // }
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
            _limiter.CancellationTokenSource.Cancel();
            _cb.CancellationTokenSource.Cancel();
            base.Dispose();
        }
    }
}