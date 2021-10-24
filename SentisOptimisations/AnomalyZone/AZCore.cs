using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using SentisOptimisations;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;
using VRage.Game.ObjectBuilders.Components;
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

        public void ProcessAz()
        {
            foreach (var processor in azBlockProcessors)
            {
                processor.Update();
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

                            sz.Enabled = true;
                            sz.AccessTypeFactions = MySafeZoneAccess.Blacklist;
                            sz.AccessTypeGrids = MySafeZoneAccess.Blacklist;
                            sz.AccessTypePlayers = MySafeZoneAccess.Blacklist;
                            sz.Shape = MySafeZoneShape.Sphere;
                            sz.AllowedActions = MySafeZoneAction.Damage | MySafeZoneAction.Grinding | MySafeZoneAction.Welding | MySafeZoneAction.Shooting;
                            MySafeZoneComponent component = mySafeZoneBlock.Components.Get<MySafeZoneComponent>();
                            ReflectionUtils.InvokeInstanceMethod(typeof(MySafeZoneComponent), component, "SetColor", new object []{Color.Blue});
                            ReflectionUtils.InvokeInstanceMethod(typeof(MySafeZoneComponent), component, "SetRadius", new object []{3000f});


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