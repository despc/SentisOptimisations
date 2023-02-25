using System.Reflection;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class RaycastPatch
    {
        public static void Patch(PatchContext ctx)
        {
            ctx.GetPattern(typeof(MyCameraBlock).GetMethod(
                    nameof(MyCameraBlock.Init), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                .Prefixes.Add(typeof(RaycastPatch).GetMethod("CameraInit",
                    BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static void CameraInit(MyCameraBlock __instance)
        {
            var raycastLimit = SentisOptimisationsPlugin.Config.RaycastLimit;
            if (raycastLimit < 1)
            {
                return;
            }
            __instance.BlockDefinition.RaycastDistanceLimit = raycastLimit;
        }
    }
}