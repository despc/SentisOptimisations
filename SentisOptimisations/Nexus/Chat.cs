using NLog;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using System;
using Torch.Commands;
using Torch.Managers.ChatManager;
using VRageMath;

namespace SentisOptimisationsPlugin
{
  public class Chat
  {
    private CommandContext _context;
    private bool _mod;
    private Action<string, Color, string> _send;
    private static readonly Logger Log = LogManager.GetLogger("SO.Chat");
    private static string Author = "Server";
    private static Color ChatColor = Color.Yellow;

    public Chat(CommandContext context, bool Mod = false)
    {
      this._context = context;
      this._mod = Mod;
    }

    public Chat(Action<string, Color, string> sender) => this._send = sender;

    public void Respond(string response)
    {
      if (this._mod)
      {
        if (this._context == null)
          return;
        this._context.Respond(response, (string) null, (string) null);
      }
      else
        this.Send(response, Chat.ChatColor, Chat.Author);
    }

    private void Send(string response, Color color = default (Color), string sender = null)
    {
      if (this._context != null)
      {
        this._context.Respond(response, color, sender, (string) null);
      }
      else
      {
        if (this._send == null)
          return;
        this._send(response, color, sender);
      }
    }

    public static void Send(string response, ulong Target)
    {
      ScriptedChatMsg msg = new ScriptedChatMsg()
      {
        Author = Chat.Author,
        Text = response,
        Font = "White",
        Color = Chat.ChatColor,
        Target = Sync.Players.TryGetIdentityId(Target, 0)
      };
      Chat.Log.Info(Chat.Author + " (to " + ChatManagerServer.GetMemberName(Target) + "): " + response);
      MyMultiplayerBase.SendScriptedChatMessage(ref msg);
    }
  }
}
