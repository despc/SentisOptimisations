using System;
using System.Linq;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using Torch;
using Torch.API;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using VRage.Game.Voxels;
using VRageMath;

namespace FixTurrets
{
    public class FixTurretsPlugin : TorchPluginBase
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public override void Init(ITorchBase torch)
        {
            Log.Info("Init FixTurretsPlugin");
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
                        var voxelMapStorageName = voxelMap.StorageName;
                        byte[] storageData;
                        voxelMap.Storage.Save(out storageData);
                        IMyStorage storage = MyAPIGateway.Session.VoxelMaps.CreateStorage(storageData) as IMyStorage;
                        var voxelMapEntityId = voxelMap.EntityId + 123123123;
                        voxelMap.Close();
                        (MyAPIGateway.Session.VoxelMaps.CreateVoxelMapFromStorageName(voxelMapStorageName, "Bioresearch", positon
                        ) as MyVoxelMap).Storage = storage;
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