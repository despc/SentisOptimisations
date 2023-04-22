using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.ObjectBuilders;

namespace SentisOptimisationsPlugin.AllGridsActions
{
    public class NpcStationsPowerFix
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();


        public void RefillPowerStations()
        {
            try
            {
                MyAPIGateway.Utilities.InvokeOnGameThread((Action)(() =>
                {
                    foreach (KeyValuePair<long, MyFaction> faction in MySession.Static.Factions)
                    {
                        foreach (MyStation station in faction.Value.Stations)
                        {
                            if (station.StationEntityId != 0L &&
                                MyEntities.GetEntityById(station.StationEntityId) is MyCubeGrid entityById)
                            {
                                var mySlimBlocks = new HashSet<MySlimBlock>(entityById.GetBlocks());
                                Task.Run(() =>
                                {
                                    List<MyGasTank> tanks = new List<MyGasTank>();
                                    foreach (var mySlimBlock in mySlimBlocks)
                                    {
                                        if (mySlimBlock.FatBlock is MyBatteryBlock block)
                                        {
                                            MyAPIGateway.Utilities.InvokeOnGameThread((Action)(() =>
                                            {
                                                block.CurrentStoredPower = block.MaxStoredPower;
                                                
                                            }));
                                        }

                                        if (mySlimBlock.FatBlock is MyReactor reactor)
                                        {
                                            MyAPIGateway.Utilities.InvokeOnGameThread((Action)(() =>
                                            {
                                                var myInventory = reactor.GetInventory();
                                                var definitionId = new MyDefinitionId(typeof(MyObjectBuilder_Ingot),
                                                    "Uranium");
                                                var content =
                                                    (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer
                                                        .CreateNewObject(
                                                            definitionId);
                                                MyObjectBuilder_InventoryItem inventoryItem =
                                                    new MyObjectBuilder_InventoryItem
                                                        { Amount = 1000, Content = content };
                                                myInventory.AddItems(100, inventoryItem);
                                            }));
                                        }

                                        if (mySlimBlock.FatBlock is MyGasTank tank)
                                        {
                                            tanks.Add(tank);
                                            MyAPIGateway.Utilities.InvokeOnGameThread((Action)(() =>
                                            {
                                                tank.ChangeFillRatioAmount(1);
                                            }));
                                        }
                                    }
                                    for (var i = 0; i < tanks.Count; i++)
                                    {
                                        if (i == 0) continue;
                                        var myGasTank = tanks[i];
                                        MyAPIGateway.Utilities.InvokeOnGameThread((Action)(() =>
                                        {
                                            myGasTank.Enabled = false;
                                        }));
                                    }
                                });
                            }
                        }
                    }
                }));
            }
            catch (Exception e)
            {
                Log.Error("NpcStationsPowerFix error", e);
            }
        }
    }
}