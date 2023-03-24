using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;

namespace FixTurrets.Perfomance
{
    [PatchShim]
    public class ParallelUpdateTweaks
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static Dictionary<long, int> ResourceDistributorCounters = new Dictionary<long, int>();

        private static Type MyThrusterBlockThrustComponentType =
            typeof(MyParallelEntityUpdateOrchestrator).Assembly.GetType(
                "Sandbox.Game.EntityComponents.MyThrusterBlockThrustComponent");

        private static MethodInfo GetEntityMethod = MyThrusterBlockThrustComponentType.GetProperty("Entity",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).GetMethod;

        public static void Patch(PatchContext ctx)
        {
            var MethodThrustUpdateBeforeSimulation = MyThrusterBlockThrustComponentType.GetMethod
                ("UpdateBeforeSimulation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            ctx.GetPattern(MethodThrustUpdateBeforeSimulation).Prefixes.Add(
                typeof(ParallelUpdateTweaks).GetMethod(nameof(MethodThrustUpdateBeforeSimulationPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool MethodThrustUpdateBeforeSimulationPatched(Object __instance)
        {
            MyCubeGrid grid = (MyCubeGrid)GetEntityMethod.Invoke(__instance, new object[] { });

            if (grid.IsStatic)
            {
                return false;
            }

            return true;
        }
    }
}