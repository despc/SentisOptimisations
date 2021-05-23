// Decompiled with JetBrains decompiler
// Type: Torch.SharedLibrary.Utils.FactionUtils
// Assembly: SKO-GridPCULimiter, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 763A9EB8-F42B-4B0E-ACDE-740B34BF1092
// Assembly location: C:\Users\idesp\Downloads\sko-gridpculimiter-v1.2.1\SKO-GridPCULimiter.dll

using Sandbox.Game;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace SentisOptimisations
{
    public class FactionUtils
    {
        public static IMyFaction GetFaction(long factionId) => MySession.Static.Factions.TryGetFactionById(factionId);

        public static bool IsFriendlyWith(long sourcePlayerId, long targetPlayerId)
        {
            int num = 500;
            IMyFaction factionOfPlayer1 = GetFactionOfPlayer(sourcePlayerId);
            IMyFaction factionOfPlayer2 = GetFactionOfPlayer(targetPlayerId);
            if (factionOfPlayer1 != null && factionOfPlayer2 != null)
                return !MyVisualScriptLogicProvider.AreFactionsEnemies(factionOfPlayer1.Tag, factionOfPlayer2.Tag);
            return factionOfPlayer1 == null && factionOfPlayer2 != null
                ? MyVisualScriptLogicProvider.GetRelationBetweenPlayerAndFaction(sourcePlayerId,
                    factionOfPlayer2.Tag) >= num
                : factionOfPlayer2 == null && factionOfPlayer1 != null &&
                  MyVisualScriptLogicProvider.GetRelationBetweenPlayerAndFaction(targetPlayerId,
                      factionOfPlayer1.Tag) >= num;
        }

        public static IMyFaction GetFaction(string factionTag) =>
            (IMyFaction) MySession.Static.Factions.TryGetFactionByTag(factionTag, (IMyFaction) null);

        public static IMyFaction GetFactionOfPlayer(long playerId) =>
            MySession.Static.Factions.TryGetPlayerFaction(playerId);

        public static string GetFactionTagOfPlayer(long playerId)
        {
            IMyFaction factionOfPlayer = GetFactionOfPlayer(playerId);
            return factionOfPlayer == null ? "" : factionOfPlayer.Tag;
        }

        public static bool HavePlayersSameFaction(long playerA_Id, long playerB_Id) =>
            GetFactionOfPlayer(playerA_Id) == GetFactionOfPlayer(playerB_Id);

        public static bool ExistsFaction(long factionId) => GetFaction(factionId) != null;

        public static bool ExistsFaction(string factionTag) => GetFaction(factionTag) != null;
    }
}