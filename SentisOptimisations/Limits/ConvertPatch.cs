using System;
using System.Reflection;
using Sandbox.Game.Entities;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class ConvertPatch
    {
        public static void Patch(PatchContext ctx) =>
            ctx.GetPattern(typeof(MyCubeGrid).GetMethod(
                    nameof(MyCubeGrid.OnConvertToDynamic)))
                .Prefixes.Add(typeof(ConvertPatch).GetMethod("ConvertToDynamicPatch",
                    BindingFlags.Static | BindingFlags.NonPublic));

        private static void ConvertToDynamicPatch(MyCubeGrid __instance)
        {
            try
            {
                var pcu = GridUtils.GetPCU(__instance, true,
                    SentisOptimisationsPlugin.Config.IncludeConnectedGrids);
                var configMaxDinamycGridPcu = SentisOptimisationsPlugin.Config.MaxDinamycGridPCU;
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
                if (!SentisOptimisationsPlugin.Config.EnabledPcuLimiter ||
                    pcu > configMaxDinamycGridPcu)
                {
                    PcuLimiter.SendLimitMessage(owner, pcu, configMaxDinamycGridPcu, __instance.DisplayName);
                }
                
                configMaxDinamycGridPcu = enemyAround ? configMaxDinamycGridPcu : configMaxDinamycGridPcu + 5000;
                if (!SentisOptimisationsPlugin.Config.EnabledPcuLimiter ||
                    pcu <= configMaxDinamycGridPcu)
                {
                    return;
                }
                PcuLimiter.LimitReached(__instance);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e);
            }
        }
    }
}