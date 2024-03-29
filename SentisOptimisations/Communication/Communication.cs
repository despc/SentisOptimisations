﻿using System;
using Sandbox.ModAPI;

namespace SentisOptimisationsPlugin
{
  public static class Communication
  {
    public const ushort NETWORK_ID = 10777;

    public static void RegisterHandlers()
    {
      MyAPIGateway.Multiplayer.RegisterMessageHandler((ushort) NETWORK_ID, new Action<byte[]>(MessageHandler));
      SentisOptimisationsPlugin.Log.Warn("Register communication handlers");
    }

    public static void UnregisterHandlers() => MyAPIGateway.Multiplayer.UnregisterMessageHandler((ushort) NETWORK_ID, new Action<byte[]>(MessageHandler));

    private static void MessageHandler(byte[] bytes)
    {
      try
      {
        MessageType messageType = (MessageType) bytes[0];
        SentisOptimisationsPlugin.Log.Warn(string.Format("Received message: {0}: {1}", (object) bytes[0], (object) messageType));
        byte[] data = new byte[bytes.Length - 1];
        Array.Copy((Array) bytes, 1, (Array) data, 0, data.Length);
        switch (messageType)
        {
          // case MessageType.FixShip:
          //   FixShip(data);
          //   break;          
        }
      }
      catch (Exception ex)
      {
        SentisOptimisationsPlugin.Log.Warn(string.Format("Error during message handle! {0}", (object) ex));
      }
    }

    
    public static void BroadcastToClients(MessageType type, byte[] data)
    {
      byte[] newData = new byte[data.Length + 1];
      newData[0] = (byte) type;
      data.CopyTo((Array) newData, 1);
      SentisOptimisationsPlugin.Log.Warn(string.Format("Sending message to others: {0}", (object) type));
      MyAPIGateway.Utilities.InvokeOnGameThread((Action) (() => MyAPIGateway.Multiplayer.SendMessageToOthers((ushort) NETWORK_ID, newData)));
    }

    public static void SendToClient(MessageType type, byte[] data, ulong recipient)
    {
      byte[] newData = new byte[data.Length + 1];
      newData[0] = (byte) type;
      data.CopyTo((Array) newData, 1);
      SentisOptimisationsPlugin.Log.Warn(string.Format("Sending message to {0}: {1}", (object) recipient, (object) type));
      MyAPIGateway.Utilities.InvokeOnGameThread((Action) (() => MyAPIGateway.Multiplayer.SendMessageTo((ushort) NETWORK_ID, newData, recipient)));
    }
  }
}
