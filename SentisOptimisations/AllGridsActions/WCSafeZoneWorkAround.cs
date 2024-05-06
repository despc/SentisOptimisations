using System;
using System.Collections.Generic;
using NLog;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using SentisOptimisations.DelayedLogic;

namespace SentisOptimisationsPlugin.AllGridsActions
{
    public class WCSafeZoneWorkAround
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();


        public void ResizeSZ(HashSet<MySafeZone> safezones)
        {
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

                            if (mySafeZone.Shape == MySafeZoneShape.Sphere)
                            {
                                mySafeZone.Radius = mySafeZone.Radius - 0.1f;
                                MySessionComponentSafeZones.RequestUpdateSafeZone(
                                    (MyObjectBuilder_SafeZone)mySafeZone.GetObjectBuilder(false));
                            }
                            else
                            {
                                var size = mySafeZone.Size;
                                size.X = size.X - 0.1f;
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
                                        if (mySafeZone.Shape == MySafeZoneShape.Sphere)
                                        {
                                            mySafeZone.Radius = mySafeZone.Radius + 0.1f;
                                            MySessionComponentSafeZones.RequestUpdateSafeZone(
                                                (MyObjectBuilder_SafeZone)mySafeZone.GetObjectBuilder(false));
                                        }
                                        else
                                        {
                                            var size = mySafeZone.Size;
                                            size.X = size.X + 0.1f;
                                            mySafeZone.Size = size;
                                            MySessionComponentSafeZones.RequestUpdateSafeZone(
                                                (MyObjectBuilder_SafeZone)mySafeZone.GetObjectBuilder(false));
                                        }
                                    }
                                    catch
                                    {
                                    }
                                });
                            });
                        }
                        catch (Exception e)
                        {
                            Log.Error("ResizeSZ exception ", e);
                        }
                    });
                }
            }
            catch (Exception e)
            {
                Log.Error("ResizeSZ exception ", e);
            }
        }
    }
}