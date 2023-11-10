using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin.Freezer
{
    [PatchShim]
    public static class DamagePatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var MethodPerformDeformation = typeof(MyGridPhysics).GetMethod
                ("PerformDeformation", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodPerformDeformation).Prefixes.Add(
                typeof(DamagePatch).GetMethod(nameof(PatchPerformDeformation),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool PatchPerformDeformation(
            MyGridPhysics __instance)
        {
            var cubeGrid = __instance.Entity as MyCubeGrid;
            if (cubeGrid == null)
            {
                return true;
            }

            if (FreezeLogic.FrozenGrids.Contains(cubeGrid.EntityId))
            {
                return false;
            }

            return true;
        }
    }
}