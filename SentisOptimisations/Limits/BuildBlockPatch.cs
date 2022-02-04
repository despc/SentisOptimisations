using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Network;
using VRageMath;

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
        }

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
            var pcu = GridUtils.GetPCU((IMyCubeGrid)__instance, true,
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
            
            bool enemyAround = false;
            var owner = PlayerUtils.GetOwner(__instance);
            foreach (var player in PlayerUtils.GetAllPlayers())
            {
                    
                if (player.GetRelationTo(owner) != MyRelationsBetweenPlayerAndBlock.Enemies)
                {
                    continue;
                }
                var distance = Vector3D.Distance(player.GetPosition(), __instance.PositionComp.GetPosition());
                if (distance > 15000)
                {
                    continue;
                }

                enemyAround = true;
            }
            if (pcu > maxPcu)
            {
                PcuLimiter.SendLimitMessage(identityId, pcu, maxPcu, __instance.DisplayName);
            }
            
            maxPcu = enemyAround ? maxPcu : maxPcu + 5000;

            CheckBeacon(__instance);
            
            if (pcu <= maxPcu)
            {
                return true;
            }
            
            //SentisOptimisationsPlugin._limiter.LimitReached(__instance, identityId);
            return false;
        }

        private static void CheckBeacon(MyCubeGrid grid)
        {
            var myCubeGrids = GridUtils.GetSubGrids(grid);
            foreach (var myCubeGrid in myCubeGrids)
            {
                var beacon = ((MyCubeGrid)myCubeGrid).GetFirstBlockOfType<MyBeacon>();
                if (beacon != null)
                {
                    return;
                }
            }
            var beacon2 = grid.GetFirstBlockOfType<MyBeacon>();
            if (beacon2 != null)
            {
                return;
            }

            foreach (var gridBigOwner in grid.BigOwners)
            {
                MyVisualScriptLogicProvider.ShowNotification("На постройке " + grid.DisplayName + " не установлен маяк",
                    5000, "Red",
                    gridBigOwner);
                MyVisualScriptLogicProvider.ShowNotification("она будет удалена при следующей очистке", 5000, "Red",
                    gridBigOwner);
                MyVisualScriptLogicProvider.ShowNotification("There is no beacon on the structure " + grid.DisplayName,
                    5000, "Red",
                    gridBigOwner);
                MyVisualScriptLogicProvider.ShowNotification("it will be removed on next cleanup", 5000, "Red",
                    gridBigOwner);
            }
        }
    }
}