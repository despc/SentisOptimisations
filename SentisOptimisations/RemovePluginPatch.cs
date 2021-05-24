using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Network;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class RemovePluginPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var Method = typeof(MyBlockLimits).GetMethod("RemoveBlocksBuiltByID",
                BindingFlags.Static | BindingFlags.Public);

            ctx.GetPattern(Method).Prefixes.Add(
                typeof(RemovePluginPatch).GetMethod(nameof(RemoveGridPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool RemoveGridPatched(long gridEntityId, long identityID)
        {
            try
            {
                if (!MyEventContext.Current.IsLocallyInvoked &&
                    (long) MySession.Static.Players.TryGetSteamId(identityID) !=
                    (long) MyEventContext.Current.Sender.Value)
                    (MyMultiplayer.Static as MyMultiplayerServerBase).ValidationFailed(
                        MyEventContext.Current.Sender.Value, true, (string) null, true);
                else
                {
                    var method = typeof(MyBlockLimits).GetMethod("GetGridFromId",
                        BindingFlags.Static | BindingFlags.NonPublic);
                    MyCubeGrid grid = (MyCubeGrid) method.Invoke(obj: null, parameters: new object[] {gridEntityId});

                    List<IMyPlayer> players = new List<IMyPlayer>();
                    MyAPIGateway.Players.GetPlayers(players,
                        player => player.GetRelationTo(identityID) == MyRelationsBetweenPlayerAndBlock.Enemies);

                    foreach (var myPlayer in players)
                    {
                        if (Vector3D.Distance(grid.PositionComp.GetPosition(), myPlayer.GetPosition()) > 10000)
                        {
                            continue;
                        }

                        return false;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("Exception in time RemoveGridPatched", e);
            }

            return true;
        }
    }
}