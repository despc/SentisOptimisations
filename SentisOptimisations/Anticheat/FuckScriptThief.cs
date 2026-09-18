using System;
using System.Collections.Generic;
using System.Reflection;
using System.Xml.Serialization;
using NLog;
using ParallelTasks;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Replication;
using Sandbox.Game.World;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Library.Collections;
using VRage.Network;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Serialization;
using VRage.Utils;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class FuckScriptThief
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static Type MyCubeGridReplicableType =
            typeof(MyTerminalBlock).Assembly.GetType("Sandbox.Game.Replication.MyCubeGridReplicable");

        private static PropertyInfo gridPropertyInfo =
            MyCubeGridReplicableType.GetProperty("Grid", BindingFlags.Instance | BindingFlags.NonPublic);
        // Sandbox.Game\Sandbox\Game\Replication\MyCharacterReplicable.cs:30
        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("FuckScriptThief", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            // MyCubeGridReplicable.Serialize
            var Serialize = MyCubeGridReplicableType.GetMethod("Serialize",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                ;
            ctx.GetPattern(Serialize).Prefixes.Add(
                typeof(FuckScriptThief).GetMethod(nameof(SerializePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool SerializePatched(MyExternalReplicable __instance, BitStream stream,
            HashSet<string> cachedData,
            Endpoint forClient,
            Action writeData)
        {
            MyCubeGrid Grid = (MyCubeGrid) gridPropertyInfo.GetValue(__instance);
            if (Grid.Closed)
                return false;

            MyPlayer player;
            MySession.Static.Players.TryGetPlayerBySteamId(forClient.Id.Value, out player);
            if (player == null || player.Identity == null)
            {
                // No player yet (or an identity-less client): vanilla would send the grid, so the
                // stream must not be dropped - it would never finish for that client.
                Log.Warn("replicating grid " + Grid.EntityId + " to a client without a player");
                return true;
            }

            var access = AccessFor(Grid, player.Identity.IdentityId);
            MyObjectBuilder_EntityBase builder;
            try
            {
                builder = GridStreamBuilders.Get(Grid, (IMyReplicable)__instance, forClient, access,
                    MySession.Static.GameplayFrameCounter);
            }
            catch (Exception e)
            {
                Log.Error(e, "grid builder for streaming failed, using vanilla");
                return true;
            }

            stream.WriteBool(false);
            MyReplicationServer replicationServer =
                (MyReplicationServer) ReflectionUtils.InvokeStaticMethod(typeof(MyMultiplayer), "GetReplicationServer",
                    new object[] { });
            double time = replicationServer.GetClientRelevantServerTimestamp(forClient).Milliseconds;
            Parallel.Start((Action) (() =>
            {
                try
                {
                    MySerializer.Write<MyObjectBuilder_EntityBase>(stream, ref builder,
                        MyObjectBuilderSerializerKeen.Dynamic);
                }
                catch (Exception ex)
                {
                    XmlSerializer serializer = MyXmlSerializerManager.GetSerializer(builder.GetType());
                    MyLog.Default.WriteLine("Grid data - START");
                    try
                    {
                        serializer.Serialize(MyLog.Default.GetTextWriter(), (object) builder);
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "Serrialize grid failed");
                    }

                    MyLog.Default.WriteLine("Grid data - END");
                    throw;
                }

                stream.WriteDouble(time);
                writeData();
            }));

            return false;
        }

        /// <summary>How much of the grid's scripts this player may see.</summary>
        private static GridStreamBuilders.Access AccessFor(MyCubeGrid grid, long requestFromIdentity)
        {
            if (PlayerUtils.IsAdmin(requestFromIdentity)) return GridStreamBuilders.Access.Full;
            var owner = PlayerUtils.GetOwner(grid);
            if (owner == 0 || owner == requestFromIdentity) return GridStreamBuilders.Access.Full;
            IMyFaction ownerFaction = FactionUtils.GetFactionOfPlayer(owner);
            IMyFaction requestFromFaction = FactionUtils.GetFactionOfPlayer(requestFromIdentity);
            return ownerFaction != null && ownerFaction == requestFromFaction
                ? GridStreamBuilders.Access.Faction
                : GridStreamBuilders.Access.None;
        }
    }
}