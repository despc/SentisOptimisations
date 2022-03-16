
using System;
using System.Collections.Generic;
using NLog;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin
{
    public static class NexusSupport
    {
        private const ushort SOPluginId = 8701;
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static CharacterUtilities.GpsSender gpsSender = new CharacterUtilities.GpsSender();
        private static int ThisServerID = -1;
        private static bool RequireTransfer = true;

        public static NexusAPI API { get; } = new NexusAPI((ushort) 8701);

        public static bool RunningNexus { get; private set; } = false;

        public static void Init()
        {
            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler((ushort) 8701,
                new Action<ushort, byte[], ulong, bool>(ReceivePacket));
            ThisServerID = NexusAPI.GetThisServer().ServerID;
            Log.Info("SO -> Nexus integration has been initilized with serverID " +
                     ThisServerID.ToString());
            if (!NexusAPI.IsRunningNexus())
                return;
            Log.Error("Running Nexus!");
            RunningNexus = true;
            RequireTransfer = true;
            Log.Info("SO -> This server is Non-Sectored!");
        }

        public static void Dispose() => MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler((ushort) 8701,
            new Action<ushort, byte[], ulong, bool>(ReceivePacket));

        private static void ReceivePacket(
            ushort HandlerId,
            byte[] Data,
            ulong SteamID,
            bool FromServer)
        {
            if (!FromServer)
                return;
            NexusMessage SOMessage;
            try
            {
                SOMessage = MyAPIGateway.Utilities.SerializeFromBinary<NexusMessage>(Data);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Invalid Nexus cross-server message for SO",
                    Array.Empty<object>());
                return;
            }

            List<IMyPlayer> players = new List<IMyPlayer>();
            switch (SOMessage.Type)
            {
                case NexusMessageType.Chat:
                    MyAPIGateway.Players.GetPlayers(players);
                    foreach (var myPlayer in players)
                    {
                        ScriptedChatMsg msg = new ScriptedChatMsg()
                        {
                            Author = SOMessage.Sender,
                            Text = SOMessage.Response,
                            Font = "White",
                            Color = SOMessage.Color,
                            Target = myPlayer.IdentityId
                        };
                        MyMultiplayerBase.SendScriptedChatMessage(ref msg);
                    }

                    break;
                case NexusMessageType.SendGPS:
                    // MyAPIGateway.Players.GetPlayers(players);
                    // foreach (var myPlayer in players)
                    // {
                    //     // gpsSender.SendGps(asteroidSpawnerMessage.Position, asteroidSpawnerMessage.Name,
                    //     //     myPlayer.IdentityId);
                    // }

                    break;
                case NexusMessageType.Hud:
                    var message = $"The {SOMessage.Faction} faction earned points for exploring the anomaly zone";
                    MyVisualScriptLogicProvider.ShowNotificationToAll(
                        message,
                        5000, "Green");
                    break;
                default:
                    Log.Error(
                        "Invalid Nexus cross-server message for AsteroidSpawner (unrecognized type: " +
                        SOMessage.Type.ToString() + ")");
                    break;
            }
        }
    }
}