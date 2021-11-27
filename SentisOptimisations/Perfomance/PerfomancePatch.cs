using System;
using System.Linq;
using System.Reflection;
using NAPI;
using NLog;
using ParallelTasks;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SpaceEngineers.Game.Weapons.Guns;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.Components;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class PerfomancePatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly Random Random = new Random();
        public static void Patch(PatchContext ctx)
        {

            var MethodIsTargetInSz = typeof(MyLargeTurretBase).GetMethod
                (nameof(MyLargeTurretBase.IsTargetInSafeZone), BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodIsTargetInSz).Prefixes.Add(
                typeof(PerfomancePatch).GetMethod(nameof(MethodIsTargetInSzPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var methodInfos = typeof(MyLargeTurretBase).GetMethods(BindingFlags.Instance | BindingFlags.Public);
            var infos = methodInfos.Where(info => info.Name.Equals("CanShoot")).ToList();

            ctx.GetPattern(infos[1]).Prefixes.Add(
                typeof(PerfomancePatch).GetMethod(nameof(MethodCanShootPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            ctx.GetPattern(infos[0]).Prefixes.Add(
                typeof(PerfomancePatch).GetMethod(nameof(MethodCanShoot2Patched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodUpdateAfterSimulation10 = typeof(MyFunctionalBlock).GetMethod
                ("UpdateAfterSimulation10", BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodUpdateAfterSimulation10).Prefixes.Add(
                typeof(PerfomancePatch).GetMethod(nameof(MethodUpdateAfterSimulation10Patched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
          
            
            var MethodUpdateAfterSimulation100 = typeof(MyFunctionalBlock).GetMethod
                ("UpdateAfterSimulation100", BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodUpdateAfterSimulation100).Prefixes.Add(
                typeof(PerfomancePatch).GetMethod(nameof(MethodUpdateAfterSimulation100Patched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
        }


        private static bool MethodUpdateAfterSimulation10Patched(MyFunctionalBlock __instance)
        {
            return DoAdaptiveSlowdown(__instance);
        }
        private static bool MethodUpdateAfterSimulation100Patched(MyFunctionalBlock __instance)
        {
            return DoAdaptiveSlowdown(__instance);
        }

        private static bool DoAdaptiveSlowdown(MyFunctionalBlock __instance)
        {
            if (!SentisOptimisationsPlugin.Config.Adaptiveblockslowdown)
            {
                return true;
            }

            if (!(__instance is MyShipToolBase ||
                  __instance is MyShipDrill ||
                  __instance is MyGasTank ||
                  __instance is MyGasGenerator ||
                  __instance is MyAssembler ||
                  __instance is MyLargeMissileTurret ||
                  __instance is MyLargeGatlingTurret ||
                  __instance is MyLargeInteriorTurret ||
                  __instance is MyRefinery))
            {
                return true;
            }

            var staticCpuLoad = MySandboxGame.Static.CPULoad;
            if (staticCpuLoad < 60)
            {
                return true;
            }

            var next = Random.Next(60, SentisOptimisationsPlugin.Config.AdaptiveBlockSlowdownThreshold);

            if (next > staticCpuLoad)
            {
                return true;
            }

            return false;
        }
        
        private static bool MethodIsTargetInSzPatched(ref bool __result)
        {
            __result = false;
            return false;
        }

        private static bool MethodCanShootPatched(MyLargeTurretBase __instance, MyShootActionEnum action,
            long shooter,
            ref MyGunStatusEnum status, ref bool __result)
        {
            if (!__instance.HasPlayerAccess(shooter, MyRelationsBetweenPlayerAndBlock.NoOwnership))
            {
                status = MyGunStatusEnum.AccessDenied;
                __result = false;
                return false;
            }

            if (action == MyShootActionEnum.PrimaryAction)
            {
                status = MyGunStatusEnum.OK;
                __result = true;
                return false;
            }

            status = MyGunStatusEnum.Failed;
            __result = false;
            return false;
        }
        
        private static bool MethodCanShoot2Patched(MyLargeTurretBase __instance, ref MyGunStatusEnum status, ref bool __result)
        {
            var m_gunBase = ((MyGunBase)__instance.easyGetField("m_gunBase"));
            if (!m_gunBase.HasAmmoMagazines || __instance.IsControlledByLocalPlayer && MySession.Static.CameraController != __instance)
            {
                status = MyGunStatusEnum.Failed;
                __result = false;
                return false;
            }

            bool HasEnoughAmmo = (bool) __instance.easyCallMethod("HasEnoughAmmo", new object [0]{});
            if (!MySession.Static.CreativeMode && !HasEnoughAmmo)
            {
                status = MyGunStatusEnum.OutOfAmmo;
                m_gunBase.SwitchAmmoMagazineToFirstAvailable();
                __result = false;
                return false;
            }
            status = MyGunStatusEnum.OK;
            __result = true;
            return false;
        }
    }
}