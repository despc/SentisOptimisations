using System;
using System.Collections.Generic;
using NLog;
using NLog.Fluent;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using SentisOptimisations.DelayedLogic;
using VRage.Library.Utils;

namespace SentisOptimisationsPlugin.AllGridsActions
{
    public class WCSafeZoneWorkAround
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private int _cooldown = 1;

        public void ResizeSZ(HashSet<MySafeZone> safezones)
        {
            if (_cooldown < 5)
            {
                _cooldown++;
                return;
            }

            _cooldown = 1;
            try
            {
                foreach (var mySafeZone in safezones)
                {
                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    {
                        try
                        {
                            if (mySafeZone == null)
                            {
                                return;
                            }

                            if (!mySafeZone.Enabled)
                            {
                                return;
                            }
                            // Log.Error($"Refresh sz - {mySafeZone}");
                            if (mySafeZone.Shape == MySafeZoneShape.Sphere)
                            {
                                mySafeZone.Radius = mySafeZone.Radius - 1f;
                                MySessionComponentSafeZones.RequestUpdateSafeZone(
                                    (MyObjectBuilder_SafeZone)mySafeZone.GetObjectBuilder(false));
                            }
                            else
                            {
                                var size = mySafeZone.Size;
                                size.X = size.X - 1f;
                                mySafeZone.Size = size;
                                MySessionComponentSafeZones.RequestUpdateSafeZone(
                                    (MyObjectBuilder_SafeZone)mySafeZone.GetObjectBuilder(false));
                            }

                            DelayedProcessor.Instance.AddDelayedAction(DateTime.Now.AddSeconds(3), () =>
                            {
                                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                                {
                                    try
                                    {
                                        // Log.Error($"Restore sz - {mySafeZone}");
                                        if (mySafeZone.Shape == MySafeZoneShape.Sphere)
                                        {
                                            mySafeZone.Radius = mySafeZone.Radius + 1f;
                                            MySessionComponentSafeZones.RequestUpdateSafeZone(
                                                (MyObjectBuilder_SafeZone)mySafeZone.GetObjectBuilder(false));
                                        }
                                        else
                                        {
                                            var size = mySafeZone.Size;
                                            size.X = size.X + 1f;
                                            mySafeZone.Size = size;
                                            MySessionComponentSafeZones.RequestUpdateSafeZone(
                                                (MyObjectBuilder_SafeZone)mySafeZone.GetObjectBuilder(false));
                                        }
                                    }
                                    catch
                                    {
                                    }
                                }, StartAt: (int)(MySandboxGame.Static.SimulationFrameCounter + (ulong)MyRandom.Instance.Next(10,120)));
                            });
                        }
                        catch (Exception e)
                        {
                            Log.Error("ResizeSZ exception ", e);
                        }
                    }, StartAt: (int)(MySandboxGame.Static.SimulationFrameCounter + (ulong)MyRandom.Instance.Next(10,120)));
                }
            }
            catch (Exception e)
            {
                Log.Error("ResizeSZ exception ", e);
            }
        }
    }
}