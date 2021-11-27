using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using VRage.Game.Voxels;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [Category("so")]
    public class AdminCommands : CommandModule
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();


        [Command("refresh_asters", ".", null)]
        [Permission(MyPromoteLevel.Moderator)]
        public void RefreshAsters()
        {
            Task.Run(() => { DoRefreshAsters(); });
        }

        public void DoRefreshAsters()
        {
            var configPathToAsters = SentisOptimisationsPlugin.Config.PathToAsters;
            IEnumerable<IMyVoxelMap> voxelMaps = MyEntities.GetEntities().OfType<IMyVoxelMap>();
            var myVoxelMaps = MyEntities.GetEntities().OfType<IMyVoxelMap>().ToArray<IMyVoxelMap>();
            for (int i = 0; i < myVoxelMaps.Count(); i++)
            {
                try
                {
                    var voxelMap = myVoxelMaps[i];
                    var voxelMapStorageName = voxelMap.StorageName;
                    if (string.IsNullOrEmpty(voxelMapStorageName))
                    {
                        continue;
                    }

                    var asteroidName = voxelMapStorageName + ".vx2";
                    //Log.Error("start refresh aster " + asteroidName);
                    var pathToAster = configPathToAsters + "\\" + asteroidName;
                    if (!File.Exists(pathToAster))
                    {
                        continue;
                    }
                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    {
                        try
                        {
                            Vector3D position = voxelMap.PositionComp.GetPosition();
                            //Log.Error("position1 " + position);
                            byte[] bytes = File.ReadAllBytes(pathToAster);
                            voxelMap.Close();
                            IMyStorage newStorage = MyAPIGateway.Session.VoxelMaps.CreateStorage(bytes) as IMyStorage;
                            var addVoxelMap = MyWorldGenerator.AddVoxelMap(voxelMapStorageName, (MyStorageBase) newStorage, position);
                            addVoxelMap.PositionComp.SetPosition(position);
                            //Log.Error("position2 " + addVoxelMap.PositionComp.GetPosition());
                            //Log.Error("refresh aster successful" + asteroidName);
                        }
                        catch (Exception e)
                        {
                            Log.Error("Exception ", e);
                        }
                    });
                }
                catch (Exception e)
                {
                    Log.Error("Exception ", e);
                }
            }
        }
    }
}