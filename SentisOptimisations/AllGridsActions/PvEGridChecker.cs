using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SentisOptimisation.PveZone;
using VRageMath;

namespace SentisOptimisationsPlugin.AllGridsActions
{
    public class PvEGridChecker
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public void CheckGridIsPvE(MyCubeGrid grid)
        {
            if (!SentisOptimisationsPlugin.Config.PvEZoneEnabled)
            {
                return;
            }
            try
            {
                if (grid.DisplayName.Contains("Container MK-"))
                    return;
                
                if (grid.MarkedForClose || grid.Closed)
                {
                    if (PvECore.EntitiesInZone.Contains(grid.EntityId))
                        PvECore.EntitiesInZone.Remove(grid.EntityId);
                }

                
                // var owner = (grid.BigOwners.Count > 0) ? grid.BigOwners.FirstOrDefault() : 0L;
                
                // if (owner != 0L && MySession.Static.Players.IdentityIsNpc(owner))
                // {
                //     if (SentisOptimisationsPlugin.Config.EnableDamageFromNPC)
                //     {
                //         if (PvECore.EntitiesInZone.Contains(grid.EntityId))
                //             PvECore.EntitiesInZone.Remove(grid.EntityId);
                //         return;
                //     }
                // }
                
                var insidePvEZone = PvECore.PveSphere.Contains(grid.PositionComp.GetPosition()) == ContainmentType.Contains;

                if (!insidePvEZone)
                {
                    if (PvECore.EntitiesInZone.Contains(grid.EntityId))
                    {
                        PvECore.EntitiesInZone.Remove(grid.EntityId);
                    }
                }

                if (insidePvEZone)
                {
                    PvECore.EntitiesInZone.Add(grid.EntityId);
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }
    }
}