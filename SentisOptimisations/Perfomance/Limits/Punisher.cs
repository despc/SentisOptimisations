using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Physics;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SentisOptimisations;
using VRage.Game.ModAPI;
using VRage.Network;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    public class Punisher
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static Punisher __instance = new Punisher();

        private Dictionary<long, int> timeToFixDictionary = new Dictionary<long, int>();
        private Dictionary<long, int> timeToAlarmDictionary = new Dictionary<long, int>();
        public void AlertPlayerGrid(List<IMyCubeGrid> grids)
        {
            var minEntityId = grids.MinBy(grid => grid.EntityId).EntityId;
            string gridNames = string.Join(", ", grids.Select(grid => grid.DisplayName));
            var ownerId = PlayerUtils.GetOwner(grids);
            var playerName = PlayerUtils.GetPlayerIdentity(ownerId).DisplayName;

            
            if (timeToAlarmDictionary.ContainsKey(minEntityId))
            {
                timeToAlarmDictionary[minEntityId] = timeToAlarmDictionary[minEntityId] + 1;
            }
            else
            {
                timeToAlarmDictionary[minEntityId] = 1;
            }
            
            if (timeToAlarmDictionary[minEntityId] > SentisOptimisationsPlugin.Config.PhysicsChecksBeforePunish / 2)
            {
                ChatUtils.SendTo(ownerId,
                    "Warning. Grid(s) " + gridNames + " has an increased impact on server performance,\n" +
                    " when the load increases, the structure will be converted into a station");
                MyVisualScriptLogicProvider.ShowNotification(
                    "Warning. Grid(s) " + gridNames + " has an increased impact on server performance,\n" +
                    " when the load increases, the structure will be converted into a station", 5000,
                    "Red",
                    ownerId);
                timeToAlarmDictionary.Remove(minEntityId);
            }
            
            
            
            Log.Warn("Grid(s) " + gridNames + " of player " + playerName +
                     " make some physics problems");
        }

        public void PunishPlayerGrid(List<IMyCubeGrid> grids)
        {
            var minEntityId = grids.MinBy(grid => grid.EntityId).EntityId;
            string gridNames = string.Join(", ", grids.Select(grid => grid.DisplayName));
            var ownerId = PlayerUtils.GetOwner(grids);
            var playerIdentity = PlayerUtils.GetPlayerIdentity(ownerId);
            var playerName = playerIdentity == null ? "---" : playerIdentity.DisplayName;
            ChatUtils.SendTo(ownerId,
                "Attention. Grid(s) " + gridNames + " has a HUGE impact on server performance");
            MyVisualScriptLogicProvider.ShowNotification(
                "Attention. Grid(s) " + gridNames + " has a HUGE impact on server performance", 10000,
                "Red",
                ownerId);

            
            Log.Error("Grid " + gridNames + " of player " + playerName +
                      " make lot of physics problems");

            if (timeToFixDictionary.ContainsKey(minEntityId))
            {
                timeToFixDictionary[minEntityId] = timeToFixDictionary[minEntityId] + 1;
            }
            else
            {
                timeToFixDictionary[minEntityId] = 1;
            }
            
            if (timeToFixDictionary[minEntityId] > SentisOptimisationsPlugin.Config.PhysicsChecksBeforePunish)
            {
                foreach (var grid in grids)
                {
                    ConvertToStatic((MyCubeGrid) grid);
                    timeToFixDictionary.Remove(minEntityId);
                }
            }
        }

        public void PunishPlayerGridImmediately(List<IMyCubeGrid> grids)
        {
            foreach (var grid in grids)
            {
                ConvertToStatic((MyCubeGrid) grid);
            }
            string gridNames = string.Join(", ", grids.Select(grid => grid.DisplayName));
            var ownerId = PlayerUtils.GetOwner(grids);
            var playerIdentity = PlayerUtils.GetPlayerIdentity(ownerId);
            var playerName = playerIdentity == null ? "---" : playerIdentity.DisplayName;
            Log.Error("Grid(s) " + gridNames + " of player " + playerName +
                      " make lot of physics problems");
        }

        private void ConvertToStatic(MyCubeGrid myCubeGrid)
        {
            if (!myCubeGrid.IsStatic)
            {
                myCubeGrid.Physics?.SetSpeeds(Vector3.Zero, Vector3.Zero);
                myCubeGrid.ConvertToStatic();
                CommunicationUtils.SyncConvert(myCubeGrid, true);
                try
                {
                    MyMultiplayer.RaiseEvent<MyCubeGrid>(myCubeGrid, (MyCubeGrid x) => new Action(x.ConvertToStatic),
                        default(EndpointId));
                    foreach (var player in MySession.Static.Players.GetOnlinePlayers())
                    {
                        MyMultiplayer.RaiseEvent<MyCubeGrid>(myCubeGrid,
                            (MyCubeGrid x) => new Action(x.ConvertToStatic), new EndpointId(player.Id.SteamId));
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "()Exception in RaiseEvent.");
                }

                if (myCubeGrid.BigOwners.Count > 0)
                {
                    ChatUtils.SendTo(myCubeGrid.BigOwners[0],
                        "Grid " + myCubeGrid.DisplayName + " converted to station cause high performance issue");
                    MyVisualScriptLogicProvider.ShowNotification(
                        "Grid " + myCubeGrid.DisplayName + " converted to station cause high performance issue", 10000,
                        "Red",
                        myCubeGrid.BigOwners[0]);
                }

                Log.Error("Grid " + myCubeGrid.DisplayName + " Converted To Static");
            }
        }
    }
}