using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Torch.Commands;
using Torch.Commands.Permissions;
using Torch.Managers;
using VRage.Game.ModAPI;
using VRage.Game.Voxels;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [Category("so")]
    public class AdminCommands : CommandModule
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static MethodInfo _factionChangeSuccessInfo = typeof(MyFactionCollection).GetMethod("FactionStateChangeSuccess", BindingFlags.NonPublic | BindingFlags.Static);

        

        [Command("cf", ".", null)]
        [Permission(MyPromoteLevel.Moderator)]
        public void CleanFactions()
        {
            
            foreach (var faction in MySession.Static.Factions.ToList())
            {
                Log.Error("init clean faction " + faction.Value.Tag);
                if (faction.Value.Tag.Length < 7)
                {
                    continue;
                }

                if (faction.Value.Members.Count > 1)
                {
                    continue;
                }
                Log.Error("DELETE faction " + faction.Value.Tag);
                cleanFaction(faction);
            }
        }

        private static void cleanFaction(KeyValuePair<long, MyFaction> faction)
        {
            NetworkManager.RaiseStaticEvent(_factionChangeSuccessInfo, MyFactionStateChange.RemoveFaction,
                faction.Value.FactionId, faction.Value.FactionId, 0L, 0L);
            if (!MyAPIGateway.Session.Factions.FactionTagExists(faction.Value.Tag)) return;
            MyAPIGateway.Session.Factions.RemoveFaction(faction.Value.FactionId);
        }

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