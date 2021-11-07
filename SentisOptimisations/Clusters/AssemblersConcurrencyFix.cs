using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRage.Utils;

namespace FixTurrets.Clusters
{
    [PatchShim]
    public static class AssemblersConcurrencyFix
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static Random r = new Random();
        
        public static void Patch(PatchContext ctx)
        {
            var MethodGetMasterAssembler = typeof(MyAssembler).GetMethod
                ("GetMasterAssembler", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodGetMasterAssembler).Prefixes.Add(
                typeof(AssemblersConcurrencyFix).GetMethod(nameof(GetMasterAssemblerPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodUpdateAssembleMode = typeof(MyAssembler).GetMethod
                ("UpdateAssembleMode", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodUpdateAssembleMode).Prefixes.Add(
                typeof(AssemblersConcurrencyFix).GetMethod(nameof(UpdateAssembleModePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }


        private static bool UpdateAssembleModePatched()
        {
            var pullItemsSlowdown = SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.AssemblerPullItemsSlowdown;
            var chance = 1 / pullItemsSlowdown;
            var run = r.NextDouble() <= chance;
            if (run)
            {
                return true;
            }

            return false;
        }

        private static bool GetMasterAssemblerPatched(MyAssembler __instance, ref MyAssembler __result)
        {
            try
            {
                List<IMyConveyorEndpoint> m_conveyorEndpoints = new List<IMyConveyorEndpoint>();

                Predicate<IMyConveyorEndpoint> m_vertexPredicate = new Predicate<IMyConveyorEndpoint>(
                    vertex => vertex.CubeBlock is MyAssembler && vertex.CubeBlock != __instance);
                Predicate<IMyConveyorEndpoint> m_edgePredicate = new Predicate<IMyConveyorEndpoint>(edge =>
                    edge.CubeBlock.OwnerId == 0L || __instance.FriendlyWithBlock(edge.CubeBlock));
                MyGridConveyorSystem.FindReachable(__instance.ConveyorEndpoint, m_conveyorEndpoints, m_vertexPredicate,
                    m_edgePredicate);
                m_conveyorEndpoints.ShuffleList<IMyConveyorEndpoint>();
                foreach (IMyConveyorEndpoint conveyorEndpoint in m_conveyorEndpoints)
                {
                    if (conveyorEndpoint.CubeBlock is MyAssembler cubeBlock && !cubeBlock.DisassembleEnabled &&
                        !cubeBlock.IsSlave)
                    {
                        List<MyProductionBlock.QueueItem> m_queue =
                            (List<MyProductionBlock.QueueItem>) ReflectionUtils.GetInstanceField(typeof(MyAssembler),
                                cubeBlock, "m_queue");
                        if (m_queue.Count > 0)
                        {
                            __result = cubeBlock;
                            return false;
                        }
                    }
                }

                __result = null;
            }
            catch (Exception e)
            {
                Log.Error(e);
                return true;
            }

            return false;
        }
    }
}