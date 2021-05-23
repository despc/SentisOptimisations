using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Sandbox.Engine.Multiplayer;
using VRage.GameServices;
using VRage.Network;
using VRage.Serialization;

namespace SentisOptimisations
{
    public static class NetworkUtils
    {
        public static IPAddress GetIPAddressOfClient(ulong steamId)
        {
            try
            {
                return new IPAddress(((IEnumerable<byte>) BitConverter.GetBytes(new MyP2PSessionState().RemoteIP))
                    .Reverse<byte>().ToArray<byte>());
            }
            catch (Exception ex)
            {
                return (IPAddress) null;
            }
        }

        public static Dictionary<ulong, short> GetPings()
        {
            Dictionary<ulong, short> dictionary = new Dictionary<ulong, short>();
            if (MyMultiplayer.Static != null && MyMultiplayer.Static.ReplicationLayer != null)
            {
                SerializableDictionary<ulong, short> pings;
                ((MyReplicationServer) MyMultiplayer.Static.ReplicationLayer).GetClientPings(out pings);
                dictionary = pings.Dictionary;
            }

            return dictionary;
        }
    }
}