using System;
using System.Reflection;
using NAPI;
using NLog;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRage.Game.Definitions.Reputation;
using VRage.Game.ObjectBuilders.Definitions.Reputation;
using VRage.Utils;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class ReputationPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly Random Random = new Random();

        public static void Patch(PatchContext ctx)
        {
            var MethodDamageFactionPlayerReputation = typeof(MyFactionCollection).GetMethod
            ("DamageFactionPlayerReputation",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            ctx.GetPattern(MethodDamageFactionPlayerReputation).Prefixes.Add(
                typeof(ReputationPatch).GetMethod(nameof(DamageFactionPlayerReputationPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static void DamageFactionPlayerReputationPatched(
            long playerIdentityId,
            long attackedIdentityId,
            MyReputationDamageType repDamageType,
            MyFactionCollection __instance)
        {
            if (!Sync.IsServer || attackedIdentityId == 0L)
                return;
            if (MySession.Static == null || MySession.Static.Factions == null)
            {
                MyLog.Default.Error("Session.Static or MySession.Static.Factions is null. Should not happen!");
            }
            else
            {
                MyFaction playerFaction1 = MySession.Static.Factions.GetPlayerFaction(MyPirateAntennas.GetPiratesId());

                var HarkonnenFaction = MySession.Static.Factions.TryGetFactionByTag("HRKN");
                var AtreidesFaction = MySession.Static.Factions.TryGetFactionByTag("ATRD");

                MyFaction attackedFaktion = (MyFaction)__instance.TryGetPlayerFaction(attackedIdentityId);
                if (attackedFaktion == null && playerFaction1 != null)
                {
                    int reputationDamageDelta = GetReputationDamageDelta(repDamageType, __instance, true);
                    __instance.AddFactionPlayerReputation(playerIdentityId, playerFaction1.FactionId,
                        reputationDamageDelta, false);
                }
                else
                {
                    if (attackedFaktion == null || attackedFaktion.IsMember(playerIdentityId))
                        return;
                    if (AtreidesFaction != null && HarkonnenFaction != null)
                    {
                        if (attackedFaktion.FactionId == AtreidesFaction.FactionId)
                        {
                            int reputationDamageDeltaAtr = GetReputationDamageDelta(repDamageType, __instance, false);
                            __instance.AddFactionPlayerReputation(playerIdentityId, HarkonnenFaction.FactionId,
                                reputationDamageDeltaAtr, true);
                            //__instance.SetReputationBetweenFactions();AddFactionPlayerReputation(playerIdentityId, playerFaction2.FactionId, -reputationDamageDelta, false);
                        }

                        if (attackedFaktion.FactionId == HarkonnenFaction.FactionId)
                        {
                            int reputationDamageDeltaHrkn = GetReputationDamageDelta(repDamageType, __instance, false);
                            __instance.AddFactionPlayerReputation(playerIdentityId, AtreidesFaction.FactionId,
                                reputationDamageDeltaHrkn, true);
                            //__instance.SetReputationBetweenFactions();AddFactionPlayerReputation(playerIdentityId, playerFaction2.FactionId, -reputationDamageDelta, false);
                        }
                    }

                    int reputationDamageDelta =
                        GetReputationDamageDelta(repDamageType, __instance, playerFaction1 == attackedFaktion);
                    __instance.AddFactionPlayerReputation(playerIdentityId, attackedFaktion.FactionId,
                        -reputationDamageDelta, false);
                    if (playerFaction1 == null || attackedFaktion == playerFaction1)
                        return;
                    __instance.AddFactionPlayerReputation(playerIdentityId, playerFaction1.FactionId,
                        reputationDamageDelta, false);
                }
            }
        }

        private static int GetReputationDamageDelta(MyReputationDamageType repDamageType,
            MyFactionCollection __instance, bool isPirates = false)
        {
            MyReputationSettingsDefinition m_reputationSettings =
                (MyReputationSettingsDefinition)__instance.easyGetField("m_reputationSettings");
            MyObjectBuilder_ReputationSettingsDefinition.MyReputationDamageSettings reputationDamageSettings =
                isPirates ? m_reputationSettings.PirateDamageSettings : m_reputationSettings.DamageSettings;
            int reputationDamageDelta = 0;
            switch (repDamageType)
            {
                case MyReputationDamageType.GrindingWelding:
                    reputationDamageDelta = reputationDamageSettings.GrindingWelding;
                    break;
                case MyReputationDamageType.Damaging:
                    reputationDamageDelta = reputationDamageSettings.Damaging;
                    break;
                case MyReputationDamageType.Stealing:
                    reputationDamageDelta = reputationDamageSettings.Stealing;
                    break;
                case MyReputationDamageType.Killing:
                    reputationDamageDelta = reputationDamageSettings.Killing;
                    break;
                default:
                    MyLog.Default.Error("Reputation damage type not handled. Check and update.");
                    break;
            }

            return reputationDamageDelta;
        }
    }
}