using System.Collections.Generic;
using Sandbox.Game;
using Sandbox.ModAPI;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;

namespace SentisOptimisationsPlugin.AnomalyZone
{
    public class AZReward
    {
        public static void AwardPointsAndRewards(MySafeZoneBlock szBlock, IMyFaction faction)
        {
            var config = SentisOptimisationsPlugin.Config;
            var configConfigAnomalyZone = config.ConfigAnomalyZone;
            ConfigAnomalyZone configAZ = null;
            foreach (var configAnomalyZone in configConfigAnomalyZone)
            {
                if (configAnomalyZone.BlockId == szBlock.EntityId)
                {
                    configAZ = configAnomalyZone;
                    break;
                }
            }

            if (configAZ == null)
            {
                configAZ = new ConfigAnomalyZone();
                configAZ.BlockId = szBlock.EntityId;
                configConfigAnomalyZone.Add(configAZ);
            }

            var savedPoints = configAZ.Points;
            ConfigAnomalyZonePoints fPoints = null;
            long facId = faction.FactionId;
            foreach (var factionPoints in savedPoints)
            {
                if (factionPoints.FactionId == facId)
                {
                    fPoints = factionPoints;
                }
            }

            if (fPoints == null)
            {
                fPoints = new ConfigAnomalyZonePoints();
                fPoints.FactionId = facId;
                savedPoints.Add(fPoints);
            }

            fPoints.Points = fPoints.Points + 1;
            var tmp = new ConfigAnomalyZone();
            configConfigAnomalyZone.Add(tmp);
            configConfigAnomalyZone.Remove(tmp);
            MyAPIGateway.Utilities.InvokeOnGameThread(() =>
            {
                DoReward(faction, szBlock.CubeGrid.EntityId, fPoints);
            });
            
        }

        private static void DoReward(IMyFaction faction, long gridEntityId, ConfigAnomalyZonePoints fPoints)
        {
            IMyCubeGrid grid = MyAPIGateway.Entities.GetEntityById(gridEntityId) as IMyCubeGrid;
            var gts = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid);
            var blockList = new List<IMyTerminalBlock>();


            gts.GetBlocksOfType<IMyTerminalBlock>(blockList);

            foreach (var block in blockList)
            {
                //MyVisualScriptLogicProvider.SendChatMessage("blocks blocklist");
                if (block != null && block is IMyCargoContainer && block.CustomData.Contains("Prizebox"))
                {
                    if (fPoints.Points >= 0)
                    {
                        if (fPoints.Points % 1 == 0)
                        {
                            var definitionId = new MyDefinitionId(typeof(MyObjectBuilder_PhysicalObject), "SpaceCredit");
                            var content =
                                (MyObjectBuilder_PhysicalObject) MyObjectBuilderSerializer.CreateNewObject(definitionId);
                            int countComponents = 125000;
                            MyObjectBuilder_InventoryItem inventoryItem = new MyObjectBuilder_InventoryItem
                                {Amount = countComponents, Content = content};

                            if (block.GetInventory().CanItemsBeAdded(countComponents, definitionId) == true)
                            {
                                block.GetInventory().AddItems(countComponents, inventoryItem.Content);
                            }
                        }
                    }

                    if (fPoints.Points >= 200)
                    {
                        if (fPoints.Points % 6 == 0)
                        {
                         
                        }
                    }
                }
            }

            MyVisualScriptLogicProvider.ShowNotificationToAll(
                $"The {faction.Name} faction earned points for exploring the anomaly zone",
                5000, "Green");
        }
    }
}