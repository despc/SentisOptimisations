using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations;

namespace FixTurrets.Garage
{
    public class OldGridProcessor
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public void OnLoaded()
        {
            CheckOldGridAsync();
        }

        public async void CheckOldGridAsync()
        {
            try
            {
                Log.Info("Check old grids started");
                try
                {
                    await Task.Delay(30000);
                    var myCubeGrids = MyEntities.GetEntities().OfType<MyCubeGrid>();
                    await Task.Run(() => { CheckAllGrids(myCubeGrids); });
                }
                catch (Exception e)
                {
                    Log.Error("Check old grids Error", e);
                }
            }
            catch (Exception e)
            {
                Log.Error("Check old grids start Error", e);
            }
        }


        private void CheckAllGrids(IEnumerable<MyCubeGrid> myCubeGrids)
        {
            foreach (var myCubeGrid in myCubeGrids)
            {
                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    try
                    {
                        if (myCubeGrid.DisplayName.Contains("@"))
                        {
                            return;
                        }
                        var bigOwners = myCubeGrid.BigOwners;
                        if (bigOwners == null || bigOwners.Count == 0)
                        {
                            return;
                        }

                        var owner = bigOwners[0];
                        var steamId = MySession.Static.Players.TryGetSteamId(owner);
                        if (steamId == 0)
                        {
                            return;
                        }

                        var identityById = PlayerUtils.GetIdentityById(owner);
                        var lastLogoutTime = identityById.LastLogoutTime;
                        var totalDays = (DateTime.Now - lastLogoutTime).TotalDays;
                        if (totalDays > 14)
                        {
                            Log.Warn("Товарища " + owner + " нет с нами уже " + totalDays +
                                     " дней, приберём его грид " +
                                     myCubeGrid.DisplayName + " в гараж");
                            GarageCore.Instance.MoveToGarage(owner, myCubeGrid);
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error("Check old grid EXCEPTION", e);
                    }
                });
            }
        }
    }
}