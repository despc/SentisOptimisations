using System;
using System.Collections.Generic;
using System.Reflection;
using Havok;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using SentisOptimisation.PveZone;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class DamagePatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static Dictionary<long, long> contactInfo = new Dictionary<long, long>();
        public static HashSet<long> protectedChars = new HashSet<long>();
        private static bool _init;

        public static void Init()
        {
            if (_init)
                return;
            _init = true;
            MyAPIGateway.Session.DamageSystem.RegisterBeforeDamageHandler(0, ProcessDamage);
        }

        private static void ProcessDamage(object target, ref MyDamageInformation info)
        {
            try
            {
                DoProcessDamage(target, ref info);
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private static void DoProcessDamage(object target, ref MyDamageInformation damage)
        {
            IMyCharacter character = target as IMyCharacter;
            if (character != null)
            {
                if (protectedChars.Contains(character.EntityId))
                {
                    damage.Amount = 0;
                    return;
                }
            }
        }
        public static void Patch(PatchContext ctx)
        {
            var MethodPerformDeformation = typeof(MyGridPhysics).GetMethod
                ("PerformDeformation", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodPerformDeformation).Prefixes.Add(
                typeof(DamagePatch).GetMethod(nameof(PatchPerformDeformation),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool PatchPerformDeformation(
            MyGridPhysics __instance,
            ref HkBreakOffPointInfo pt,
            bool fromBreakParts,
            float separatingVelocity,
            MyEntity otherEntity)
        {
            var cubeGrid = (__instance.Entity as MyCubeGrid);
            if (cubeGrid == null)
            {
                return true;
            }

            if (otherEntity is MyVoxelBase)
            {
                if (cubeGrid.PlayerPresenceTier != MyUpdateTiersPlayerPresence.Normal ||
                    !SentisOptimisationsPlugin.Config.NoDamageFromVoxelsIfNobodyNear)
                {
                    return false;
                }

                if (separatingVelocity < SentisOptimisationsPlugin.Config.NoDamageFromVoxelsBeforeSpeed)
                {
                    if (separatingVelocity < 5)
                    {
                        try
                        {
                            if (contactInfo.ContainsKey(cubeGrid.EntityId))
                            {
                                contactInfo[cubeGrid.EntityId] = contactInfo[cubeGrid.EntityId] + 1;
                            }
                            else
                            {
                                contactInfo[cubeGrid.EntityId] = 1;
                            }
                        }
                        catch (Exception e)
                        {
                            //do nothing
                        }
                    }

                    return false;
                }
            }


            if (SentisOptimisationsPlugin.Config.PvEZoneEnabled)
            {
                if (PvECore.EntitiesInZone.Contains(cubeGrid.EntityId))
                {
                    if (SentisOptimisationsPlugin.Config.EnableDamageFromNPC
                        && otherEntity is MyCubeGrid && ((MyCubeGrid)otherEntity).isNpcGrid())
                    {
                        return true;
                    }
                    return false;
                }
            }

            if (cubeGrid.IsStatic)
            {
                return SentisOptimisationsPlugin.Config.StaticRamming;
            }

            if (otherEntity is MyCubeGrid)
            {
                if (((MyCubeGrid)otherEntity).IsStatic)
                {
                    return true;
                }

                if (((MyCubeGrid)otherEntity).Mass < SentisOptimisationsPlugin.Config.MinimumMassForKineticDamage)
                {
                    return false;
                }
            }

            return true;
        }
    }
}