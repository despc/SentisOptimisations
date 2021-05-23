using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRage.Network;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class BuildBlockPatch
    {
        public static void Patch(PatchContext ctx)
        {
            MethodInfo method = typeof(MyCubeGrid).GetMethod("BuildBlocksRequest",
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            ctx.GetPattern(method).Prefixes.Add(typeof(BuildBlockPatch).GetMethod("BuildBlocksRequest",
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic));
            // MethodInfo methodGridChanged = typeof(MyCubeGrid).GetMethod("RaiseGridChanged",
            //     BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            // ctx.GetPattern(methodGridChanged).Prefixes.Add(typeof(BuildBlockPatch).GetMethod("PatchRaiseGridChanged",
            //     BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic));
        }

        // private static void PatchRaiseGridChanged(MyCubeGrid __instance)
        // {
        //     SentisOptimisationsPlugin.Log.Error("PatchRaiseGridChanged " + __instance.DisplayName);
        //     if (GridUtils.GetPCU(__instance, true,
        //             SentisOptimisationsPlugin.Config.IncludeConnectedGrids) <
        //         (__instance.IsStatic
        //             ? SentisOptimisationsPlugin.Config.MaxStaticGridPCU
        //             : SentisOptimisationsPlugin.Config.MaxDinamycGridPCU))
        //     {
        //         SentisOptimisationsPlugin._limiter.LimitNotReached(__instance);
        //     }
        // }


        private static bool BuildBlocksRequest(
            MyCubeGrid __instance,
            HashSet<MyCubeGrid.MyBlockLocation> locations)
        {
            
            if (!SentisOptimisationsPlugin.Config.EnabledPcuLimiter)
                return true;
            if (__instance == null)
            {
                SentisOptimisationsPlugin.Log.Warn("BuildBlocksRequest: Grid is NULL.");
                return true;
            }
            if (MyDefinitionManager.Static.GetCubeBlockDefinition(
                locations.FirstOrDefault().BlockDefinition) == null)
            {
                SentisOptimisationsPlugin.Log.Warn("BuildBlocksRequest: Definition is NULL.");
                return true;
            }

            long identityId = PlayerUtils.GetIdentityByNameOrId(MyEventContext.Current.Sender.Value.ToString())
                .IdentityId;
            var pcu = GridUtils.GetPCU(__instance, true,
                SentisOptimisationsPlugin.Config.IncludeConnectedGrids);
            var instanceIsStatic = __instance.IsStatic;
            var maxPcu = instanceIsStatic
                ? SentisOptimisationsPlugin.Config.MaxStaticGridPCU
                : SentisOptimisationsPlugin.Config.MaxDinamycGridPCU;

            var subGrids = GridUtils.GetSubGrids(__instance);
            foreach (var myCubeGrid in subGrids)
            {
                if (myCubeGrid.IsStatic)
                {
                    maxPcu = SentisOptimisationsPlugin.Config.MaxStaticGridPCU;
                }
            }
            MyCubeBlockDefinition cubeBlockDefinition = MyDefinitionManager.Static.GetCubeBlockDefinition(locations.First().BlockDefinition);
            pcu = pcu + cubeBlockDefinition.PCU;
            if (pcu <= maxPcu)
            {
                return true;
            }
            PcuLimiter.SendLimitMessage(identityId, pcu, maxPcu);
            //SentisOptimisationsPlugin._limiter.LimitReached(__instance, identityId);
            return false;
        }
    }
}