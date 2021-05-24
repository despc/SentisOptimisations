using System;
using System.Reflection;
using SentisOptimisations;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class MergeBlockPatch
    {
        public static void Patch(PatchContext ctx) =>
            ctx.GetPattern(typeof(MyShipMergeBlock).GetMethod("UpdateBeforeSimulation10",
                    BindingFlags.Instance | BindingFlags.Public))
                .Prefixes.Add(typeof(MergeBlockPatch).GetMethod("MergeCheck",
                    BindingFlags.Static | BindingFlags.NonPublic));

        private static bool MergeCheck(MyShipMergeBlock __instance)
        {
            try
            {
           
            
                if (__instance?.Other == null)
                {
                    return true;
                }


                var pcu = GridUtils.GetPCU(__instance.CubeGrid, true,
                              SentisOptimisationsPlugin.Config.IncludeConnectedGrids)
                          + GridUtils.GetPCU(__instance.Other.CubeGrid, true,
                              SentisOptimisationsPlugin.Config.IncludeConnectedGrids);
                var maxPcu = __instance.CubeGrid.IsStatic
                    ? SentisOptimisationsPlugin.Config.MaxStaticGridPCU
                    : SentisOptimisationsPlugin.Config.MaxDinamycGridPCU;
                var subGrids = GridUtils.GetSubGrids(__instance.CubeGrid);
                foreach (var myCubeGrid in subGrids)
                {
                    if (myCubeGrid.IsStatic)
                    {
                        maxPcu = SentisOptimisationsPlugin.Config.MaxStaticGridPCU;
                    }
                }

                if (__instance.Other.CubeGrid.IsStatic)
                {
                    maxPcu = SentisOptimisationsPlugin.Config.MaxStaticGridPCU;
                }

                if (!SentisOptimisationsPlugin.Config.EnabledPcuLimiter ||
                    SentisOptimisationsPlugin.Config.AllowMerge ||
                    __instance.IsLocked || !__instance.IsFunctional || !__instance.Other.IsFunctional
                    || pcu <= maxPcu)
                    return true;
                __instance.Enabled = false;
                var owner = PlayerUtils.GetOwner(__instance.CubeGrid);
                PcuLimiter.SendLimitMessage(owner, pcu, maxPcu, __instance.CubeGrid.DisplayName);
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return true;
            }
        }
    }
}