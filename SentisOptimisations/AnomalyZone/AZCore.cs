using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Components;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisOptimisationsPlugin.AnomalyZone
{
    public class AZCore
    {
        public CancellationTokenSource CancellationTokenSource { get; set; }
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static readonly HashSet<long> ImmortalGrids = new HashSet<long>();

        HashSet<MySafeZoneBlock> azBlocks = new HashSet<MySafeZoneBlock>();
        HashSet<AZBlockProcessor> azBlockProcessors = new HashSet<AZBlockProcessor>();

        public void Init()
        {
            CancellationTokenSource = new CancellationTokenSource();
            StartAzProcessing();
        }
        
        public void OnUnloading()
        {
            CancellationTokenSource.Cancel();
        }

        public void StartAzProcessing()
        {
            StartAzProcessingLoop();
            StartAzWeekRewardLoop();
        }

        private async void StartAzProcessingLoop()
        {
            try
            {
                Log.Info("AzProcessingLoop started");
                while (!CancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(1000);
                        await Task.Run(FindAz);
                        await Task.Run(ProcessAz);
                    }
                    catch (Exception e)
                    {
                        Log.Error("AzProcessingLoop Error", e);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("AzProcessingLoop start Error", e);
            }
        }
        
        private async void StartAzWeekRewardLoop()
        {
            try
            {
                Log.Info("AzWeekRewardLoop started");
                while (!CancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(60000);
                        await Task.Run(CheckAzWeekReward);
                    }
                    catch (Exception e)
                    {
                        Log.Error("AzWeekRewardLoop Error", e);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("AzWeekRewardLoop start Error", e);
            }
        }

        public void ProcessAz()
        {
            foreach (var processor in azBlockProcessors)
            {
                processor.Update();
            }
        }

        public void CheckAzWeekReward()
        {
            var now = DateTime.Now;
            if (now.DayOfWeek == DayOfWeek.Sunday && now.Hour == 23 && now.Minute < 3)
            {
                SentisOptimisationsPlugin.Config.AzWinners = "";
                var configConfigAnomalyZone = SentisOptimisationsPlugin.Config.ConfigAnomalyZone;
                foreach (var configAnomalyZone in configConfigAnomalyZone)
                {
                    if ((now - configAnomalyZone.LastWeekWinnerSavedTime).TotalDays > 5)
                    {
                        configAnomalyZone.LastWeekWinnerSavedTime = now;
                        long factionId = 0;
                        int maxPoints = 0;
                        foreach (var configAnomalyZonePoints in configAnomalyZone.Points)
                        {
                            if (configAnomalyZonePoints.Points > maxPoints)
                            {
                                maxPoints = configAnomalyZonePoints.Points;
                                factionId = configAnomalyZonePoints.FactionId;

                            }
                        }
                        configAnomalyZone.LastWeekWinnerFactionId = factionId;
                    }
                    
                }
                
                foreach (var configAnomalyZone in configConfigAnomalyZone)
                {
                    var factionId = configAnomalyZone.LastWeekWinnerFactionId;
                    IMyFaction f = MySession.Static.Factions.TryGetFactionById(factionId);
                    if (SentisOptimisationsPlugin.Config.AzWinners.Length == 0)
                    {
                        SentisOptimisationsPlugin.Config.AzWinners = f.Tag;
                    }
                    else
                    {
                        SentisOptimisationsPlugin.Config.AzWinners = SentisOptimisationsPlugin.Config.AzWinners + ":" + f.Tag;
                    }
                }
               
                foreach (var configAnomalyZone in SentisOptimisationsPlugin.Config.ConfigAnomalyZone)
                {
                    configAnomalyZone.Points.Clear();
                }
                var tmp = new ConfigAnomalyZone();
                SentisOptimisationsPlugin.Config.ConfigAnomalyZone.Add(tmp);
                SentisOptimisationsPlugin.Config.ConfigAnomalyZone.Remove(tmp);
            }
        }

        public void FindAz()
        {
            foreach (var sz in MySessionComponentSafeZones.SafeZones)
            {
                var safeZoneBlockId = sz.SafeZoneBlockId;
                if (safeZoneBlockId == null)
                {
                    continue;
                }
                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    try
                    {
                        MySafeZoneBlock mySafeZoneBlock = (MySafeZoneBlock) MyEntities.GetEntityById(safeZoneBlockId);
                        if (mySafeZoneBlock == null)
                        {
                            return;
                        }
                        var ownerId = mySafeZoneBlock.OwnerId;
                        if (SentisOptimisationsPlugin.Config.AzOwner != ownerId)
                        {
                            return;
                        }

                        var customData = mySafeZoneBlock.CustomData;

                        if (customData.Contains("AnomalyZoneBlock"))
                        {
                            if (azBlocks.Contains(mySafeZoneBlock))
                            {
                                return;
                            }
                            
                            var definitionId = AZReward.GetItemDefenition("Component", "ZoneChip");
                            var content =
                                (MyObjectBuilder_PhysicalObject) MyObjectBuilderSerializer.CreateNewObject(definitionId);
                            MyObjectBuilder_InventoryItem inventoryItem = new MyObjectBuilder_InventoryItem
                                {Amount = 5, Content = content};
                            if (mySafeZoneBlock.GetInventory().CanItemsBeAdded(5, definitionId))
                            {
                                mySafeZoneBlock.GetInventory().AddItems(5, inventoryItem.Content);
                            }
                            sz.Enabled = true;
                            sz.AccessTypeFactions = MySafeZoneAccess.Blacklist;
                            sz.AccessTypeGrids = MySafeZoneAccess.Blacklist;
                            sz.AccessTypePlayers = MySafeZoneAccess.Blacklist;
                            sz.Shape = MySafeZoneShape.Sphere;
                            sz.AllowedActions = MySafeZoneAction.Damage | MySafeZoneAction.Grinding | MySafeZoneAction.Welding | MySafeZoneAction.Shooting;
                            MySafeZoneComponent component = mySafeZoneBlock.Components.Get<MySafeZoneComponent>();
                            ReflectionUtils.InvokeInstanceMethod(typeof(MySafeZoneComponent), component, "SetColor", new object []{Color.Blue});
                            ReflectionUtils.InvokeInstanceMethod(typeof(MySafeZoneComponent), component, "SetRadius", new object []{30f});


                            ImmortalGrids.Add(mySafeZoneBlock.CubeGrid.EntityId);
                            azBlocks.Add(mySafeZoneBlock);
                            azBlockProcessors.Add(new AZBlockProcessor(mySafeZoneBlock, sz));
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error("Exception ", e);
                    }
                });
                
                
            }
        }
    }
}