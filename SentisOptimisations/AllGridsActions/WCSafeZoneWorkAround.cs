using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NLog;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;

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
                                mySafeZone.Radius = mySafeZone.Radius - 1;
                                MySessionComponentSafeZones.RequestUpdateSafeZone(
                                    (MyObjectBuilder_SafeZone)mySafeZone.GetObjectBuilder(false));
                            }
                            else
                            {
                                var size = mySafeZone.Size;
                                size.X = size.X - 1;
                                mySafeZone.Size = size;
                                MySessionComponentSafeZones.RequestUpdateSafeZone(
                                    (MyObjectBuilder_SafeZone)mySafeZone.GetObjectBuilder(false));
                            }

                            Task.Run(async () =>
                            {
                                await Task.Delay(3000);
                                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                                {
                                    try
                                    {
                                        if (mySafeZone.Shape == MySafeZoneShape.Sphere)
                                        {
                                            mySafeZone.Radius = mySafeZone.Radius + 1;
                                            MySessionComponentSafeZones.RequestUpdateSafeZone(
                                                (MyObjectBuilder_SafeZone)mySafeZone.GetObjectBuilder(false));
                                        }
                                        else
                                        {
                                            var size = mySafeZone.Size;
                                            size.X = size.X + 1;
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