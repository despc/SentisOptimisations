using System;
using System.Reflection;
using NLog;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using SpaceEngineers.Game.Weapons.Guns;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class PerfomancePatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly Random Random = new Random();

        public static void Patch(PatchContext ctx)
        {
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
    }
}