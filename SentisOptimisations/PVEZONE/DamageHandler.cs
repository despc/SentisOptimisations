using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.Game.ModAPI;

namespace SentisOptimisation.PveZone
{
    public static class DamageHandler
    {
        public static void Init()
        {
            if (MyAPIGateway.Session != null)
                MyAPIGateway.Session.DamageSystem.RegisterBeforeDamageHandler(0, ProcessDamage);
        }

        private static void ProcessDamage(object target, ref MyDamageInformation info)
        {
            if (!SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.PvEZoneEnabled)
                return;

            if (target == null)
                return;

            if (info.Amount == 0)
            {
                return;
            }

            var attackerId = info.AttackerId;
            long underAttackId = 0L;
            var mySlimBlock = target as MySlimBlock;

            if (mySlimBlock == null)
            {
                if (target is MyCharacter)
                    return;
            }
            else
            {
                if (!PvECore.EntitiesInZone.Contains(mySlimBlock.CubeGrid.EntityId))
                {
                    return;
                }

                underAttackId = ((mySlimBlock.CubeGrid.BigOwners.Count > 0) ? mySlimBlock.CubeGrid.BigOwners[0] : 0L);
            }

            if (MyEntities.TryGetEntityById(info.AttackerId, out var attackerEntity, allowClosed: true))
            {
                if (attackerEntity is MyVoxelBase)
                    return;

                if (attackerEntity is MyThrust)
                {
                    info.Amount = 0f;
                    info.IsDeformation = false;
                    return;
                }

                attackerId = GetAttackerId(attackerEntity);
            }

            if (attackerId == 0L)
            {
                info.Amount = 0f;
                info.IsDeformation = false;
            }
            else
            {
                if (underAttackId == 0L || underAttackId == attackerId)
                    return;

                if ((MySession.Static.Players.IdentityIsNpc(attackerId) ||
                     MySession.Static.Players.IdentityIsNpc(underAttackId))
                    && SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.EnableDamageFromNPC)
                {
                    return;
                }


                var steamId1 = MySession.Static.Players.TryGetSteamId(attackerId);
                var steamId2 = MySession.Static.Players.TryGetSteamId(underAttackId);
                var attackerFaction = MySession.Static.Factions.TryGetPlayerFaction(attackerId);
                var underAttackFaction = MySession.Static.Factions.TryGetPlayerFaction(underAttackId);

                if ((steamId1 != 0UL && steamId2 != 0UL && steamId1 == steamId2)
                    || (attackerFaction != null && underAttackFaction != null &&
                        attackerFaction.Tag == underAttackFaction.Tag))
                {
                    return;
                }

                info.Amount = 0f;
                info.IsDeformation = false;
            }
        }

        private static long GetAttackerId(MyEntity attackerEntity)
        {
            if (attackerEntity is MyHandDrill myHandDrill)
                return myHandDrill.OwnerIdentityId;

            if (attackerEntity is MyAutomaticRifleGun myAutomaticRifleGun)
            {
                return myAutomaticRifleGun.OwnerIdentityId;
            }

            if (attackerEntity is MyEngineerToolBase toolBase)
            {
                return toolBase.OwnerIdentityId;
            }

            if (attackerEntity is MyUserControllableGun myUserControllableGun)
                return myUserControllableGun.OwnerId;

            if (attackerEntity is MyCubeGrid myCubeGrid)
                return (myCubeGrid.BigOwners.Count > 0) ? myCubeGrid.BigOwners[0] : 0L;

            if (attackerEntity is MyShipToolBase myShipToolBase)
                return myShipToolBase.OwnerId;

            if (attackerEntity is MyConveyorSorter wcGun)
                return wcGun.OwnerId;

            if (attackerEntity is MyCharacter character)
            {
                return character.GetPlayerIdentityId();
            }

            if (attackerEntity is MyWarhead warheadBlock)
            {
                return warheadBlock.OwnerId;
            }

            return 0;
        }
    }
}