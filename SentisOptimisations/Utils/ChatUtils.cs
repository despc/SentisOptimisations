// Decompiled with JetBrains decompiler
// Type: Torch.SharedLibrary.Utils.ChatUtils
// Assembly: SKO-GridPCULimiter, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 763A9EB8-F42B-4B0E-ACDE-740B34BF1092
// Assembly location: C:\Users\idesp\Downloads\sko-gridpculimiter-v1.2.1\SKO-GridPCULimiter.dll

using System.Text;
using Sandbox.Game;

namespace SentisOptimisations
{
    public static class ChatUtils
    {
        public static void SendTo(long playerId, string message, string from = "", string color = "Red") =>
            MyVisualScriptLogicProvider.SendChatMessage(message, from, playerId, color);

        public static void SendTo(long playerId, StringBuilder message, string from = "", string color = "Red") =>
            MyVisualScriptLogicProvider.SendChatMessage(message.ToString(), from, playerId, color);

        public static void SendToAll(string message, string from = "", string color = "Red") =>
            MyVisualScriptLogicProvider.SendChatMessage(message, from, font: color);

        public static void SendToAll(StringBuilder message, string from = "", string color = "Red") =>
            MyVisualScriptLogicProvider.SendChatMessage(message.ToString(), from, font: color);
    }
}