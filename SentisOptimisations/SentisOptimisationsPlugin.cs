using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using Sandbox;
using Sandbox.Engine.Utils;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Torch;
using Torch.API;
using Torch.Commands;
using Torch.Commands.Permissions;
using Torch.Utils;
using VRage.Game.ModAPI;
using VRage.Game.Voxels;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    public class SentisOptimisationsPlugin : TorchPluginBase
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static Persistent<Config> _config;
        public static Config  Config => _config?.Data;

        public override void Init(ITorchBase torch)
        {
            Log.Info("Init SentisOptimisationsPlugin");
            MyFakes.ENABLE_SCRAP = false;
            SetupConfig();
        }
        
        
        public override void Update() {
            if (MySandboxGame.Static.SimulationFrameCounter % 6000 == 0)
            {
                foreach (KeyValuePair<long, MyFaction> faction in MySession.Static.Factions)
                {
                    foreach (MyStation station in faction.Value.Stations)
                    {
                        if (station.StationEntityId != 0L && MyEntities.GetEntityById(station.StationEntityId) is MyCubeGrid entityById)
                        {
                            MyBatteryBlock batteryBlock = entityById.GetFirstBlockOfType<MyBatteryBlock>();
                            if (batteryBlock == null)
                            {
                                continue;
                            }
                            batteryBlock.CurrentStoredPower = batteryBlock.MaxStoredPower;
                        }
                    }
                }
            }
        }
        
        private void SetupConfig() {

            var configFile = Path.Combine(StoragePath, "SentisOptimisations.cfg");

            try {

                _config = Persistent<Config>.Load(configFile);

            } catch (Exception e) {
                Log.Warn(e);
            }

            if (_config?.Data == null) {

                Log.Info("Create Default Config, because none was found!");

                _config = new Persistent<Config>(configFile, new Config());
                _config.Save();
            }
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
                        Vector3D positon = voxelMap.GetPosition();
                        byte[] storageData;
                        voxelMap.Storage.Save(out storageData);
                        IMyStorage storage = MyAPIGateway.Session.VoxelMaps.CreateStorage(storageData) as IMyStorage;
                        voxelMap.Close();
                        MyWorldGenerator.AddVoxelMap(voxelMap.Name, (MyStorageBase) storage, positon);
                        Log.Error("refresh voxels " + voxelMap.DisplayName);
                    }
                    catch (Exception e)
                    {
                        Log.Error("Exception ", e);
                    }
                }
            }
        }
    }
}