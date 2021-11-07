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

namespace TorchMonitor.ProfilerMonitors
{
    public class Punisher
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static Punisher __instance = new Punisher();

        private Dictionary<long, int> timeToFixDictionary = new Dictionary<long, int>();
        public void AlertPlayerGrid(IMyCubeGrid grid)
        {
            if (grid.BigOwners.Count == 0)
            {
                ConvertToStatic((MyCubeGrid) grid);
            }
            ChatUtils.SendTo(grid.BigOwners[0],
                "Предупреждение. Структура " + grid.DisplayName + " оказывает повышенное влияние на производительность сервера,\n" +
                " при увеличении нагрузки структура будет конвертирована в станцию");
            MyVisualScriptLogicProvider.ShowNotification(
                "Предупреждение. Структура " + grid.DisplayName + " оказывает повышенное влияние на производительность сервера,\n" +
                " при увеличении нагрузки структура будет конвертирована в станцию", 10000,
                "Red",
                grid.BigOwners[0]);
            
            Log.Warn("Grid " + grid.DisplayName + " of player " + PlayerUtils.GetOwner(grid) +
                     " make some physics problems");
        }

        public void PunishPlayerGrid(IMyCubeGrid grid)
        {
            if (grid.BigOwners.Count == 0)
            {
                ConvertToStatic((MyCubeGrid) grid);
            }
            
            ChatUtils.SendTo(grid.BigOwners[0],
                "Предупреждение. Структура " + grid.DisplayName + " оказывает КРАЙНЕ высокое влияние на производительность сервера");
            MyVisualScriptLogicProvider.ShowNotification(
                "Предупреждение. Структура " + grid.DisplayName + " оказывает КРАЙНЕ высокое влияние на производительность сервера", 10000,
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
            
            if (timeToFixDictionary[grid.EntityId] > SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.PhysicsChecksBeforePunish)
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
                        "Структура " + myCubeGrid.DisplayName + " конвертирована в статику в связи с дудосом");
                    MyVisualScriptLogicProvider.ShowNotification(
                        "Структура " + myCubeGrid.DisplayName + " конвертирована в статику в связи с дудосом", 10000,
                        "Red",
                        myCubeGrid.BigOwners[0]);
                }

                Log.Error("Grid " + myCubeGrid.DisplayName + " Converted To Static");
            }
        }
    }
}