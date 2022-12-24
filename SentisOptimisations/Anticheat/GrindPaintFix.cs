using System.Reflection;
using HarmonyLib;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Network;

namespace SentisOptimisationsPlugin
{
  public class GrindPaintFix
  {
    private static readonly Logger _log = LogManager.GetLogger("GrindPaintFix");

    public static void Patch()
    {
      SentisOptimisationsPlugin.harmony.Patch((MethodBase) AccessTools.Method("Sandbox.Game.Entities.MyCubeGrid:OnSetToConstructionRequest"), new HarmonyMethod(typeof (GrindPaintFix), "GrindFix"));
      SentisOptimisationsPlugin.harmony.Patch((MethodBase) AccessTools.Method("Sandbox.Game.Entities.MyCubeGrid:ColorGridOrBlockRequestValidation"), new HarmonyMethod(typeof (GrindPaintFix), "ColorFix"));
    }

    public static bool ColorFix(ref long player, ref bool __result)
    {
      MyPlayer player1;
      if (!MySession.Static.Players.TryGetPlayerBySteamId(MyEventContext.Current.Sender.Value, out player1))
      {
        __result = false;
        return false;
      }
      if (player != player1.Identity.IdentityId)
        _log.Warn(string.Format("{0} sent a recolor request with an id that isn't theirs ({1})", (object) player1.DisplayName, (object) player));
      player = player1.Identity.IdentityId;
      return true;
    }

    public static bool GrindFix(MyCubeGrid __instance)
    {
      ulong steamId = MyEventContext.Current.Sender.Value;
      MyPlayer player;
      if (MySession.Static.Players.TryGetPlayerBySteamId(steamId, out player))
        _log.Warn(string.Format("Player {0} ({1}) attempted to use the grinder exploit, canceled", (object) player.DisplayName, (object) steamId));
      return false;
    }
  }
}
