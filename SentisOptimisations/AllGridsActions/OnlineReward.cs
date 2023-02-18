using System;
using System.Collections.Generic;
using NLog;
using Sandbox.ModAPI;
using SentisOptimisations;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;

namespace SentisOptimisationsPlugin.AllGridsActions
{
    public class OnlineReward
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private Dictionary<long, DateTime> rewards = new Dictionary<long, DateTime>();

        public void RewardOnline()
        {
            try
            {
                foreach (var p in PlayerUtils.GetAllPlayers())
                {
                    if (p.IsBot)
                    {
                        continue;
                    }

                    if (p.Character == null)
                    {
                        continue;
                    }

                    var lastLoginTime = PlayerUtils.GetPlayerIdentity(p.IdentityId).LastLoginTime;
                    var totalMinutes = (DateTime.Now - lastLoginTime).TotalMinutes;
                    if (totalMinutes < SentisOptimisationsPlugin.Config.OnlineRewardEachMinutes)
                    {
                        continue;
                    }
                    var minutesAroundHour = totalMinutes % SentisOptimisationsPlugin.Config.OnlineRewardEachMinutes;
                    if (minutesAroundHour > -1 || minutesAroundHour > 1)
                    {
                        if (!rewards.ContainsKey(p.IdentityId))
                        {
                            rewards[p.IdentityId] = DateTime.Now;
                            DoReward(p);
                        }
                        else
                        {
                            var lastRewardDate = rewards[p.IdentityId];
                            if ((DateTime.Now - lastRewardDate).TotalMinutes < SentisOptimisationsPlugin.Config.OnlineRewardEachMinutes)
                            {
                                continue;
                            }

                            rewards[p.IdentityId] = DateTime.Now;
                            DoReward(p);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("online reward exception ", e);
            }
        }

        private void DoReward(IMyPlayer player)
        {
            if (!SentisOptimisationsPlugin.Config.OnlineRewardEnabled)
            {
                return;
            }

            var configOnlineReward = SentisOptimisationsPlugin.Config.OnlineReward;

            foreach (var s in configOnlineReward.Split(';'))
            {
                var type = s.Split('=')[0];

                var count = Int32.Parse(s.Split('=')[1]);

                var itemType = type.Split('_')[0];
                var itemId = type.Substring(type.IndexOf('_') + 1);
                var definitionId = GetItemDefenition(itemType, itemId);

                var content =
                    (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(definitionId);
                MyObjectBuilder_InventoryItem inventoryItem = new MyObjectBuilder_InventoryItem
                    { Amount = count, Content = content };
                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    try
                    {
                        var myInventory = player.Character.GetInventory();
                        myInventory.AddItems(count, inventoryItem.Content);
                        
                        
                    }
                    catch (Exception e)
                    {
                        Log.Error("Online reward error ", e);
                    }
                });
            }
            var rewardMessage = SentisOptimisationsPlugin.Config.OnlineRewardMessage;
            ChatUtils.SendTo(player.IdentityId, rewardMessage);
        }

        private MyDefinitionId GetItemDefenition(string type, string id)
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
            }

            return new MyDefinitionId(typeof(MyObjectBuilder_PhysicalObject), id);
        }
    }
}