using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NAPI;
using NLog;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations;
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

                MyFaction attackedFaction = (MyFaction)__instance.TryGetPlayerFaction(attackedIdentityId);
                if (attackedFaction == null && playerFaction1 != null)
                {
                    int reputationDamageDelta = GetReputationDamageDelta(repDamageType, __instance, true);
                    __instance.AddFactionPlayerReputation(playerIdentityId, playerFaction1.FactionId,
                        reputationDamageDelta, false);
                }
                else
                {
                    if (attackedFaction == null || attackedFaction.IsMember(playerIdentityId))
                        return;
                    if (AtreidesFaction != null && HarkonnenFaction != null)
                    {
                        try
                        {
                            var currentRep = MyAPIGateway.Session.Factions.GetReputationBetweenPlayerAndFaction(
                                playerIdentityId,
                                attackedFaction.FactionId);
                            var repChance = currentRep % 50;
                            if (currentRep <= -1498)
                            {
                                return;
                            }

                            if (repChance == 0 || repChance == 1)
                            {
                                if (currentRep <= -500)
                                {
                                    var factionMember = attackedFaction.Members.First();
                                    var player = PlayerUtils.GetPlayer(playerIdentityId);
                                    var guardNames = SentisOptimisationsPlugin.Config.GuardiansNpcNames.Split(',');
                                    var gridName = guardNames[Random.Next(0, guardNames.Length)];
                                    string gridFullPath = Path.Combine(SentisOptimisationsPlugin.Config.PathToGrids,
                                        gridName);
                                    NPCSpawner.DoSpawnGrids(factionMember.Value.PlayerId, gridFullPath,
                                        NPCSpawner.SpawnPosition(player.GetPosition()).Value);
                                }

                                if (currentRep <= -1000)
                                {
                                    var factionMember = attackedFaction.Members.First();
                                    var player = PlayerUtils.GetPlayer(playerIdentityId);
                                    var guardNames = SentisOptimisationsPlugin.Config.GuardiansNpcNames.Split(',');
                                    var gridName = guardNames[Random.Next(0, guardNames.Length)];
                                    string gridFullPath = Path.Combine(SentisOptimisationsPlugin.Config.PathToGrids,
                                        gridName);
                                    NPCSpawner.DoSpawnGrids(factionMember.Value.PlayerId, gridFullPath,
                                        NPCSpawner.SpawnPosition(player.GetPosition()).Value);
                                }

                                if (currentRep <= -1500)
                                {
                                    var factionMember = attackedFaction.Members.First();
                                    var player = PlayerUtils.GetPlayer(playerIdentityId);
                                    var guardNames = SentisOptimisationsPlugin.Config.GuardiansNpcNames.Split(',');
                                    var gridName = guardNames[Random.Next(0, guardNames.Length)];
                                    string gridFullPath = Path.Combine(SentisOptimisationsPlugin.Config.PathToGrids,
                                        gridName);
                                    NPCSpawner.DoSpawnGrids(factionMember.Value.PlayerId, gridFullPath,
                                        NPCSpawner.SpawnPosition(player.GetPosition()).Value);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Log.Error(e, "Spawn guard exception");
                        }
                        if (attackedFaction.FactionId == AtreidesFaction.FactionId)
                        {
                            int reputationDamageDeltaAtr = GetReputationDamageDelta(repDamageType, __instance, false);
                            __instance.AddFactionPlayerReputation(playerIdentityId, HarkonnenFaction.FactionId,
                                reputationDamageDeltaAtr, true);
                            //__instance.SetReputationBetweenFactions();AddFactionPlayerReputation(playerIdentityId, playerFaction2.FactionId, -reputationDamageDelta, false);
                        }

                        if (attackedFaction.FactionId == HarkonnenFaction.FactionId)
                        {
                            int reputationDamageDeltaHrkn = GetReputationDamageDelta(repDamageType, __instance, false);
                            __instance.AddFactionPlayerReputation(playerIdentityId, AtreidesFaction.FactionId,
                                reputationDamageDeltaHrkn, true);
                            //__instance.SetReputationBetweenFactions();AddFactionPlayerReputation(playerIdentityId, playerFaction2.FactionId, -reputationDamageDelta, false);
                        }
                    }

                    int reputationDamageDelta =
                        GetReputationDamageDelta(repDamageType, __instance, playerFaction1 == attackedFaction);
                    __instance.AddFactionPlayerReputation(playerIdentityId, attackedFaction.FactionId,
                        -reputationDamageDelta, false);
                    if (playerFaction1 == null || attackedFaction == playerFaction1)
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