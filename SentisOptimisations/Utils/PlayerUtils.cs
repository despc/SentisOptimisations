using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisOptimisations
{
    public class PlayerUtils
    {
        public static IMyPlayer GetPlayer(ulong steamId) =>
            (IMyPlayer) MySession.Static.Players.GetPlayerById(new MyPlayer.PlayerId(steamId)) ?? (IMyPlayer) null;

        public static ulong GetSteamId(IMyPlayer player) => player == null ? 0UL : player.SteamUserId;

        public static MyIdentity GetIdentityByNameOrId(string playerNameOrSteamId)
        {
            foreach (MyIdentity allIdentity in (IEnumerable<MyIdentity>) MySession.Static.Players.GetAllIdentities())
            {
                ulong result;
                if (allIdentity.DisplayName == playerNameOrSteamId || ulong.TryParse(playerNameOrSteamId, out result) &&
                    (long) MySession.Static.Players.TryGetSteamId(allIdentity.IdentityId) == (long) result)
                    return allIdentity;
            }

            return (MyIdentity) null;
        }

        public static long GetOwner(IMyCubeGrid grid)
        {
            List<long> bigOwners = grid.BigOwners;
            int count = bigOwners.Count;
            long num = 0;
            if (count > 0 && (ulong) bigOwners[0] > 0UL)
                return bigOwners[0];
            return count > 1 ? bigOwners[1] : num;
        }

        public static MyIdentity GetIdentityByName(string playerName)
        {
            foreach (MyIdentity allIdentity in (IEnumerable<MyIdentity>) MySession.Static.Players.GetAllIdentities())
            {
                if (allIdentity.DisplayName == playerName)
                    return allIdentity;
            }

            return (MyIdentity) null;
        }

        public static MyIdentity GetIdentityById(long playerId)
        {
            foreach (MyIdentity allIdentity in (IEnumerable<MyIdentity>) MySession.Static.Players.GetAllIdentities())
            {
                if (allIdentity.IdentityId == playerId)
                    return allIdentity;
            }

            return (MyIdentity) null;
        }

        public static string GetPlayerNameById(long playerId)
        {
            MyIdentity identityById = GetIdentityById(playerId);
            return identityById != null ? identityById.DisplayName : "Nobody";
        }

        public static bool IsNpc(long playerId) => MySession.Static.Players.IdentityIsNpc(playerId);

        public static bool IsPlayer(long playerId) => GetPlayerIdentity(playerId) != null;

        public static bool HasIdentity(long playerId) => MySession.Static.Players.HasIdentity(playerId);

        public static List<MyIdentity> GetAllPlayerIdentities()
        {
            if (MySession.Static == null)
                return new List<MyIdentity>();
            List<MyIdentity> list = MySession.Static.Players.GetAllIdentities().ToList<MyIdentity>();
            var npcIdentities = MySession.Static.Players.GetNPCIdentities();
            List<long> npcs = new List<long>();
            foreach (var identity in npcIdentities) npcs.Add(identity);
            return list
                .Where<MyIdentity>((Func<MyIdentity, bool>) (i =>
                    !npcs.Any<long>((Func<long, bool>) (n => n == i.IdentityId))))
                .OrderBy<MyIdentity, string>((Func<MyIdentity, string>) (i => i.DisplayName)).ToList<MyIdentity>();
        }

        public static MyIdentity GetPlayerIdentity(long identityId) => GetAllPlayerIdentities()
            .Where<MyIdentity>((Func<MyIdentity, bool>) (c => c.IdentityId == identityId)).FirstOrDefault<MyIdentity>();

        public static IMyPlayer GetPlayer(long identityId) =>
            (IMyPlayer) MySession.Static.Players.GetPlayerById(
                new MyPlayer.PlayerId(MySession.Static.Players.TryGetSteamId(identityId))) ?? (IMyPlayer) null;

        public static List<IMyPlayer> GetAllPlayers()
        {
            if (MySession.Static == null)
                return new List<IMyPlayer>();
            List<IMyPlayer> players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players);
            return players;
        }
        
        public static List<IMyPlayer> GetAllPlayersInRadius(Vector3D point, float radius)
        {
            if (MySession.Static == null)
                return new List<IMyPlayer>();
            List<IMyPlayer> players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players);
            List<IMyPlayer> result = new List<IMyPlayer>();
            foreach (IMyPlayer p in players)
            {
                try
                {
                    if (Vector3D.Distance(p.GetPosition(), point) < radius)
                    {
                        result.Add(p);
                    }
                }
                catch (Exception e)
                {
                    //do nothing
                }
            }

            return result;
        }
        
        public static bool IsAnyPlayersInRadius(Vector3D point, float radius)
        {
            return GetAllPlayersInRadius(point, radius).Count > 0;
        }

        public static bool IsAdmin(IMyPlayer player) => player != null &&
                                                        (player.PromoteLevel == MyPromoteLevel.Owner ||
                                                         player.PromoteLevel == MyPromoteLevel.Admin ||
                                                         player.PromoteLevel == MyPromoteLevel.Moderator);

        public static bool IsAdmin(long identityId)
        {
            IMyPlayer player = GetPlayer(identityId);
            return player != null && IsAdmin(player);
        }

        public static long GetIdentityIdByName(string name)
        {
            MyIdentity myIdentity = MySession.Static.Players.GetAllIdentities()
                .Where<MyIdentity>((Func<MyIdentity, bool>) (c =>
                    c.DisplayName.Equals(name, StringComparison.InvariantCultureIgnoreCase)))
                .FirstOrDefault<MyIdentity>();
            return myIdentity != null ? myIdentity.IdentityId : 0L;
        }

        public static bool IsCreateiveToolsEnabled(ulong steamId) => MySession.Static.CreativeToolsEnabled(steamId);

        public static bool IsPCULimitIgnored(ulong steamId) => MySession.Static.RemoteAdminSettings[steamId]
            .HasFlag((Enum) AdminSettingsEnum.IgnorePcu);

        public static long GetOwner(List<IMyCubeGrid> grids)
        {
            foreach (var myCubeGrid in grids)
            {
                var owner = GetOwner(myCubeGrid);
                if (owner != 0)
                {
                    return owner;
                }
            }

            return 0;
        }
    }
}