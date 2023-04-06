using System;
using System.Collections.Generic;
using NLog;
using Sandbox.Engine.Multiplayer;
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
        public void AlertPlayerGrid(IMyCubeGrid grid)
        {
            if (grid.DisplayName.Contains("@"))
            {
                return;
            }
            if (grid.BigOwners.Count == 0)
            {
                ConvertToStatic((MyCubeGrid) grid);
            }
            
            if (timeToAlarmDictionary.ContainsKey(grid.EntityId))
            {
                timeToAlarmDictionary[grid.EntityId] = timeToAlarmDictionary[grid.EntityId] + 1;
            }
            else
            {
                timeToAlarmDictionary[grid.EntityId] = 1;
            }
            
            if (timeToAlarmDictionary[grid.EntityId] > SentisOptimisationsPlugin.Config.PhysicsChecksBeforePunish / 2)
            {
                ChatUtils.SendTo(grid.BigOwners[0],
                    "Warning. Grid " + grid.DisplayName + " has an increased impact on server performance,\n" +
                    " when the load increases, the structure will be converted into a station");
                MyVisualScriptLogicProvider.ShowNotification(
                    "Warning. Grid " + grid.DisplayName + " has an increased impact on server performance,\n" +
                    " when the load increases, the structure will be converted into a station", 5000,
                    "Red",
                    grid.BigOwners[0]);
                timeToAlarmDictionary.Remove(grid.EntityId);
            }
            
            
            
            Log.Warn("Grid " + grid.DisplayName + " of player " + PlayerUtils.GetOwner(grid) +
                     " make some physics problems");
        }

        public void PunishPlayerGrid(IMyCubeGrid grid)
        {
            if (grid.DisplayName.Contains("@"))
            {
                return;
            }
            if (grid.BigOwners.Count == 0)
            {
                ConvertToStatic((MyCubeGrid) grid);
            }
            
            ChatUtils.SendTo(grid.BigOwners[0],
                "Attention. Grid " + grid.DisplayName + " has a HUGE impact on server performance");
            MyVisualScriptLogicProvider.ShowNotification(
                "Attention. Grid " + grid.DisplayName + " has a HUGE impact on server performance", 10000,
                "Red",
                grid.BigOwners[0]);
            
            Log.Error("Grid " + grid.DisplayName + " of player " + PlayerUtils.GetOwner(grid) +
                      " make lot of physics problems");

            if (timeToFixDictionary.ContainsKey(grid.EntityId))
            {
                timeToFixDictionary[grid.EntityId] = timeToFixDictionary[grid.EntityId] + 1;
            }
            else
            {
                timeToFixDictionary[grid.EntityId] = 1;
            }
            
            if (timeToFixDictionary[grid.EntityId] > SentisOptimisationsPlugin.Config.PhysicsChecksBeforePunish)
            {
                ConvertToStatic((MyCubeGrid) grid);
                timeToFixDictionary.Remove(grid.EntityId);
            }
        }

        public void PunishPlayerGridImmediately(IMyCubeGrid grid)
        {
            ConvertToStatic((MyCubeGrid) grid);
            Log.Error("Grid " + grid.DisplayName + " of player " + PlayerUtils.GetOwner(grid) +
                      " make lot of physics problems");
        }

        private void ConvertToStatic(MyCubeGrid myCubeGrid)
        {
            if (!myCubeGrid.IsStatic)
            {
                myCubeGrid.Physics?.SetSpeeds(Vector3.Zero, Vector3.Zero);
                myCubeGrid.ConvertToStatic();
                PlayerCommands.SyncConvert(myCubeGrid, true);
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