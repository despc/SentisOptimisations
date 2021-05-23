// Decompiled with JetBrains decompiler
// Type: Torch.SharedLibrary.Utils.CreditsUtils
// Assembly: SKO-GridPCULimiter, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 763A9EB8-F42B-4B0E-ACDE-740B34BF1092
// Assembly location: C:\Users\idesp\Downloads\sko-gridpculimiter-v1.2.1\SKO-GridPCULimiter.dll

using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace SentisOptimisations
{
    public static class CreditsUtils
    {
        public static bool HasSufficientCredits(long playerId, long requiredCredits)
        {
            if (MySession.Static == null)
                return false;
            long balance = GetPlayerBalance(playerId);
            if (balance >= requiredCredits)
                return true;
            FactionUtils.GetFactionOfPlayer(playerId)?.TryGetBalanceInfo(out balance);
            return balance >= requiredCredits;
        }

        public static bool AddCredits(long playerId, long creditsToAdd, bool addToFaction = true)
        {
            if (MySession.Static == null)
                return false;
            IMyPlayer player = PlayerUtils.GetPlayer(playerId);
            if (player == null)
                return false;
            if (addToFaction)
            {
                IMyFaction faction = FactionUtils.GetFaction(playerId);
                if (faction != null)
                {
                    faction.RequestChangeBalance(creditsToAdd);
                    return true;
                }
            }

            player.RequestChangeBalance(creditsToAdd);
            return true;
        }

        public static bool RemoveCredits(long playerId, long creditsToRemove)
        {
            if (MySession.Static == null)
                return false;
            IMyPlayer player = PlayerUtils.GetPlayer(playerId);
            if (player == null)
                return false;
            if (GetPlayerBalance(player) >= creditsToRemove)
            {
                player.RequestChangeBalance(-creditsToRemove);
                return true;
            }

            IMyFaction factionOfPlayer = FactionUtils.GetFactionOfPlayer(playerId);
            if (factionOfPlayer == null || GetFactionBalance(factionOfPlayer) < creditsToRemove)
                return false;
            factionOfPlayer.RequestChangeBalance(-creditsToRemove);
            return true;
        }

        public static long GetPlayerBalance(IMyPlayer player)
        {
            long balance = 0;
            player?.TryGetBalanceInfo(out balance);
            return balance;
        }

        public static long GetPlayerBalance(long playerId) =>
            CreditsUtils.GetPlayerBalance(PlayerUtils.GetPlayer(playerId));

        public static long GetFactionBalance(IMyFaction faction)
        {
            long balance = 0;
            faction?.TryGetBalanceInfo(out balance);
            return balance;
        }

        public static long GetFactionBalance(long factionId) =>
            CreditsUtils.GetFactionBalance(FactionUtils.GetFaction(factionId));

        public static long GetFactionBalance(string factionTag)
        {
            long balance = 0;
            FactionUtils.GetFaction(factionTag)?.TryGetBalanceInfo(out balance);
            return balance;
        }
    }
}