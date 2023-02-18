using NLog;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using VRageMath;

namespace SentisOptimisationsPlugin
{
  public static class CharacterUtilities
  {
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    public static readonly string m_ScanPattern = "GPS:([^:]{0,32}):([\\d\\.-]*):([\\d\\.-]*):([\\d\\.-]*):";

    public static bool TryGetPlayerBalance(ulong steamID, out long balance)
    {
      try
      {
        long identityId = MySession.Static.Players.TryGetIdentityId(steamID, 0);
        balance = MyBankingSystem.GetBalance(identityId);
        return true;
      }
      catch (Exception ex)
      {
        balance = 0L;
        return false;
      }
    }

    public static bool TryGetIdentityFromSteamID(
      this MyPlayerCollection Collection,
      ulong SteamID,
      out MyIdentity Player)
    {
      Player = Collection.TryGetPlayerIdentity(new MyPlayer.PlayerId(SteamID, 0));
      return Player != null;
    }

    public static bool TryGetPlayerSteamID(string NameOrSteamID, Chat Chat, out ulong PSteamID)
    {
      ulong result;
      if (ulong.TryParse(NameOrSteamID, out result))
      {
        if (MySession.Static.Players.TryGetPlayerIdentity(new MyPlayer.PlayerId(result, 0)) == null)
        {
          Chat?.Respond(NameOrSteamID + " doesnt exsist as an Identity! Dafuq?");
          PSteamID = 0UL;
          return false;
        }
        PSteamID = result;
        return true;
      }
      ulong? nullable;
      try
      {
        nullable = new ulong?(MySession.Static.Players.TryGetSteamId(MySession.Static.Players.GetAllIdentities().FirstOrDefault<MyIdentity>((Func<MyIdentity, bool>) (x => x.DisplayName.Equals(NameOrSteamID))).IdentityId));
      }
      catch (Exception ex)
      {
        Chat?.Respond("Player " + NameOrSteamID + " dosnt exist on the server!");
        PSteamID = 0UL;
        return false;
      }
      if (!nullable.HasValue)
      {
        Chat?.Respond("Invalid player format! Check logs for more details!");
        PSteamID = 0UL;
        return false;
      }
      PSteamID = nullable.Value;
      return true;
    }

    public static Vector3D GetGps(string text)
    {
      foreach (Match match in Regex.Matches(text, CharacterUtilities.m_ScanPattern))
      {
        string str = match.Groups[1].Value;
        double x;
        double y;
        double z;
        try
        {
          x = Math.Round(double.Parse(match.Groups[2].Value, (IFormatProvider) CultureInfo.InvariantCulture), 2);
          y = Math.Round(double.Parse(match.Groups[3].Value, (IFormatProvider) CultureInfo.InvariantCulture), 2);
          z = Math.Round(double.Parse(match.Groups[4].Value, (IFormatProvider) CultureInfo.InvariantCulture), 2);
        }
        catch (SystemException ex)
        {
          continue;
        }
        return new Vector3D(x, y, z);
      }
      return Vector3D.Zero;
    }

    public class GpsSender
    {
      private Action<Vector3D, string, long> _send;

      public GpsSender()
      {
      }

      public GpsSender(Action<Vector3D, string, long> sender) => this._send = sender;

      public void SendGps(Vector3D Position, string name, long EntityID)
      {
        if (this._send != null)
        {
          this._send(Position, name, EntityID);
        }
        else
        {
          MyGps gps = new MyGps()
          {
            ShowOnHud = true,
            Coords = Position,
            Name = name,
            Description = "Location of ... with with valuable resources",
            AlwaysVisible = true
          };
          gps.DiscardAt = new TimeSpan?(TimeSpan.FromMinutes(MySession.Static.ElapsedPlayTime.TotalMinutes + 25.0));
          gps.GPSColor = Color.Yellow;
          MySession.Static.Gpss.AddPlayerGps(EntityID, ref gps);
        }
      }
    }
  }
}
