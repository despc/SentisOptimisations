using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.ObjectBuilders;

namespace SentisOptimisationsPlugin.AllGridsActions
{
    public class NpcStationsPowerFix
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly MyDefinitionId Electricity = MyResourceDistributorComponent.ElectricityId;

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
                                    try
                                    {
                                        DoAsync(mySlimBlocks);
                                    }
                                    catch (Exception e)
                                    {
                                        Log.Error("Async exception " + e);
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

        private static void DoAsync(HashSet<MySlimBlock> mySlimBlocks)
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
                        tank.ResourceSink?.SetRequiredInputByType(Electricity, 0.000001f);
                        tank.ResourceSink?.SetMaxRequiredInputByType(Electricity, 0.000002f);
                        tank.ChangeFillRatioAmount(1);
                    }));
                }

                if (mySlimBlock.FatBlock is MyContractBlock || mySlimBlock.FatBlock is MyStoreBlock)
                {
                    var myFunctionalBlock = ((MyFunctionalBlock)mySlimBlock.FatBlock);
                    MyAPIGateway.Utilities.InvokeOnGameThread((Action)(() =>
                    {
                        myFunctionalBlock.ResourceSink?.SetRequiredInputByType(Electricity, 0.000001f);
                        myFunctionalBlock.ResourceSink?.SetMaxRequiredInputByType(Electricity, 0.000002f);
                    }));
                }
            }

            for (var i = 0; i < tanks.Count; i++)
            {
                if (i == 0) continue;
                var myGasTank = tanks[i];
                MyAPIGateway.Utilities.InvokeOnGameThread((Action)(() => { myGasTank.Enabled = false; }));
            }
        }
    }
}