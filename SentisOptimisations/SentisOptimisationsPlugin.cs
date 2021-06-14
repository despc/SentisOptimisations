using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using Sandbox;
using Sandbox.Engine.Utils;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Session;
using Torch.Commands;
using Torch.Commands.Permissions;
using Torch.Session;
using VRage.Game.ModAPI;
using VRage.Game.Voxels;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    public class SentisOptimisationsPlugin : TorchPluginBase
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static PcuLimiter _limiter = new PcuLimiter();
        private static TorchSessionManager SessionManager;
        public static Config Config;
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
        }

        private void SessionManager_SessionStateChanged(
            ITorchSession session,
            TorchSessionState newState)
        {
            if (newState == TorchSessionState.Unloading)
            {
                _limiter.OnUnloading();
            }
            else
            {
                if (newState != TorchSessionState.Loaded)
                    return;
                _limiter.OnLoaded();
            }
        }

        public override void Update()
        {
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
            base.Dispose();
        }
    }
}