using System;
using System.Collections.Generic;
using Sandbox.Game;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations;
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
            List<IMyPlayer> players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players);
            int enemies = 0;
            foreach (var myPlayer in players)
            {
                var factionOfPlayer = FactionUtils.GetFactionOfPlayer(myPlayer.IdentityId);
                if (factionOfPlayer == null)
                {
                    continue;
                }

                if (MySession.Static.Factions.AreFactionsEnemies(faction.FactionId, factionOfPlayer.FactionId))
                {
                    enemies = enemies + 1;
                }
            }
            
            var pointsToAdd = SentisOptimisationsPlugin.Config.AzPointsAddOnCaptured;
            if (SentisOptimisationsPlugin.Config.AzPointsForOnlineEnemies)
            {
                pointsToAdd = pointsToAdd + enemies;
            }
            var fPoints = ChangePoints(szBlock, faction, pointsToAdd);
            
            MyAPIGateway.Utilities.InvokeOnGameThread(() =>
            {
                DoReward(faction, szBlock.CubeGrid.EntityId);
            });
        }

        public static ConfigAnomalyZonePoints ChangePoints(MySafeZoneBlock szBlock, IMyFaction faction, int countToAdd)
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

            fPoints.Points = fPoints.Points + countToAdd;
            var tmp = new ConfigAnomalyZone();
            configConfigAnomalyZone.Add(tmp);
            configConfigAnomalyZone.Remove(tmp);
            return fPoints;
        }

        private static void DoReward(IMyFaction faction, long gridEntityId)
        {
            
            //PhysicalObject_SpaceCredit=120000;Component_ZoneChip=1
            IMyCubeGrid grid = MyAPIGateway.Entities.GetEntityById(gridEntityId) as IMyCubeGrid;
            var gts = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid);
            var blockList = new List<IMyTerminalBlock>();


            gts.GetBlocksOfType<IMyTerminalBlock>(blockList);

            foreach (var block in blockList)
            {
                if (block != null && block is IMyCargoContainer && block.CustomData.Contains("Prizebox"))
                {
                    var rewardHolders = ParseReward(SentisOptimisationsPlugin.Config.AzReward);
                    foreach (var rewardHolder in rewardHolders)
                    {
                        var itemId = rewardHolder.ItemId;
                        var type = rewardHolder.Type;
                        var configItemCount = rewardHolder.Count;

                        var definitionId = GetItemDefenition(type, itemId);
                        var content =
                            (MyObjectBuilder_PhysicalObject) MyObjectBuilderSerializer.CreateNewObject(definitionId);
                        MyObjectBuilder_InventoryItem inventoryItem = new MyObjectBuilder_InventoryItem
                            {Amount = configItemCount, Content = content};
                        if (block.GetInventory().CanItemsBeAdded(configItemCount, definitionId))
                        {
                            block.GetInventory().AddItems(configItemCount, inventoryItem.Content);
                        }
                    }
                }
            }

            MyVisualScriptLogicProvider.ShowNotificationToAll(
                $"The {faction.Name} faction earned points for exploring the anomaly zone",
                5000, "Green");
        }
        
        public class RewardHolder
        {
            private int count;
            private String itemId;
            private String type;

            public RewardHolder(string itemId, string type, int count)
            {
                this.itemId = itemId;
                this.count = count;
                this.type = type;
            }

            public int Count
            {
                get => count;
                set => count = value;
            }

            public string ItemId
            {
                get => itemId;
                set => itemId = value;
            }

            public string Type
            {
                get => type;
                set => type = value;
            }
        }
        
        public static  MyDefinitionId GetItemDefenition(string type, string id)
        {
            switch (type)
            {
                case "Component":
                {
                    return new MyDefinitionId(typeof(MyObjectBuilder_Component), id);
                }
                case "Ingot":
                {
                    return new MyDefinitionId(typeof(MyObjectBuilder_Ingot), id);
                }
                case "Ore":
                {
                    return new MyDefinitionId(typeof(MyObjectBuilder_Ore), id);
                }
                case "Ammo":
                {
                    return new MyDefinitionId(typeof(MyObjectBuilder_AmmoMagazine), id);
                }
            }

            return new MyDefinitionId(typeof(MyObjectBuilder_PhysicalObject), id);
        }
        public static List<RewardHolder> ParseReward(string waveReward)
        {
            List<RewardHolder> rewardHolders = new List<RewardHolder>();
            var rewards = waveReward.Split(';');
            foreach (var reward in rewards)
            {
                var rewardDetails = reward.Split('=');
                var typeAndId = rewardDetails[0];
                var itemType = typeAndId.Split('_')[0];
                var itemId = typeAndId.Substring(typeAndId.IndexOf('_') + 1);
                var rewardHolder = new RewardHolder(itemId, itemType, Int32.Parse(rewardDetails[1]));
                rewardHolders.Add(rewardHolder);
            }

            return rewardHolders;
        }
    }
}