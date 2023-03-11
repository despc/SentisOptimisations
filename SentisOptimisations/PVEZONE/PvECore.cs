using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRageMath;

namespace SentisOptimisation.PveZone
{
    internal static class PvECore
    {
        public static readonly Logger Log = LogManager.GetLogger("PvE ZONE");

        public static readonly HashSet<long> EntitiesInZone = new HashSet<long>();

        public static BoundingSphereD PveSphere;

        public static void Init()
        {
            var config = SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config;
            if (!config.PvEZoneEnabled)
                return;
            try
            {
                var configPveZonePos = config.PveZonePos.Split(':');
            
                Log.Info("PvE Zone pos " + config.PveZonePos);
                PveSphere = new BoundingSphereD(new Vector3D(Convert.ToDouble(configPveZonePos[0], System.Globalization.CultureInfo.InvariantCulture),
                        Convert.ToDouble(configPveZonePos[1], System.Globalization.CultureInfo.InvariantCulture), 
                        Convert.ToDouble(configPveZonePos[2], System.Globalization.CultureInfo.InvariantCulture))
                    , config.PveZoneRadius);
                DamageHandler.Init();
                Log.Info("Initing Sentis PVE ZONE... Complete!");
            }
            catch (Exception e)
            {
                Log.Error(e, "Initing Sentis PVE ZONE... CRASH!");
            }

        }
    }
}
