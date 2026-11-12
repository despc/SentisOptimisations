using System;
using System.Reflection;
using Sandbox.Game.Weapons;
using Torch.Managers.PatchManager;

namespace SentisOptimisations.Slowdowns
{
    /// <summary>
    /// A ship welder switched on makes the projections look round at once (<see cref="ProjectionSlowdown"/>): one left
    /// behind with no welder on is brought up in the next frame the welder reaches its blocks, not within a second.
    /// </summary>
    [PatchShim]
    public static class ProjectionWelderOn
    {
        public static void Patch(PatchContext ctx) => PatchGuard.Run("ProjectionWelderOn", ctx, c =>
        {
            var method = typeof(MyShipToolBase).GetMethod("OnEnabledChanged", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null)
                         ?? throw new MissingMethodException(nameof(MyShipToolBase), "OnEnabledChanged");
            c.GetPattern(method).Suffixes.Add(typeof(ProjectionWelderOn).GetMethod(nameof(Suffix), BindingFlags.Static | BindingFlags.NonPublic));
        });

        private static void Suffix(MyShipToolBase __instance) => ProjectionSlowdown.ToolEnabledChanged(__instance);
    }
}
