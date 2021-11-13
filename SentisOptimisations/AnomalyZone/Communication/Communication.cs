using System;
using Sandbox.ModAPI;

namespace SentisOptimisationsPlugin.AnomalyZone
{
  public static class Communication
  {
    public const ushort NETWORK_ID = 10654;

    public static void BroadcastToClients(MessageType type, byte[] data)
    {
      byte[] newData = new byte[data.Length + 1];
      newData[0] = (byte) type;
      data.CopyTo((Array) newData, 1);
      // SentisOptimisationsPlugin.Log.Warn(string.Format("Sending message to others: {0}", (object) type));
      MyAPIGateway.Utilities.InvokeOnGameThread((Action) (() => MyAPIGateway.Multiplayer.SendMessageToOthers((ushort) NETWORK_ID, newData)));
    }

    public static void SendToClient(MessageType type, byte[] data, ulong recipient)
    {
      byte[] newData = new byte[data.Length + 1];
      newData[0] = (byte) type;
      data.CopyTo((Array) newData, 1);
      // SentisOptimisationsPlugin.Log.Warn(string.Format("Sending message to {0}: {1}", (object) recipient, (object) type));
      MyAPIGateway.Utilities.InvokeOnGameThread((Action) (() => MyAPIGateway.Multiplayer.SendMessageTo((ushort) NETWORK_ID, newData, recipient)));
    }
  }
}
