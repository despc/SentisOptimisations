using System;
using System.Reflection;
using Havok;
using NLog;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using SentisOptimisations;
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
            var MethodLoadData = typeof(MyPhysics).GetMethod
                ("LoadData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

            ctx.GetPattern(MethodLoadData).Suffixes.Add(
                typeof(PerfomancePatch).GetMethod(nameof(MethodLoadDataPatched),
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
        
        private static void MethodLoadDataPatched(MyPhysics __instance)
        {
            var processorCount = Environment.ProcessorCount;

            var threadCount = (int)(processorCount * 0.8);
            ReflectionUtils.SetPrivateStaticField(typeof(MyPhysics), "m_threadPool", new HkJobThreadPool(threadCount));
            ReflectionUtils.SetPrivateStaticField(typeof(MyPhysics), "m_jobQueue", new HkJobQueue(threadCount + 1));
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