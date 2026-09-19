using System;
using System.Reflection;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// A wheel of a vehicle that is asleep in the physics skips its every-frame update.
    ///
    /// MyWheel.UpdateBeforeSimulation takes itself off the every-frame list only after 30 frames
    /// without ground contact, and a wheel standing on the ground never loses contact - so every
    /// parked vehicle's wheels run it every frame forever, although with the rigid body asleep it
    /// does nothing (the steering logic returns at once for an inactive body, the model swap and
    /// trail need movement). 64 parked six-wheel vehicles: 0.27 ms of every frame.
    ///
    /// While the wheel's grid is asleep the update is skipped; the wheel stays on the list, so the
    /// moment the vehicle wakes up (a pilot, a hit, anything that activates the body) the update
    /// runs again as before.
    /// </summary>
    [PatchShim]
    public static class WheelSleepUpdate
    {
        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("WheelSleepUpdate", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var wheel = typeof(MyCubeGrid).Assembly.GetType("Sandbox.Game.Entities.Blocks.MyWheel", true);
            var update = wheel.GetMethod("UpdateBeforeSimulation", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            ctx.GetPattern(update).Prefixes.Add(typeof(WheelSleepUpdate).GetMethod(nameof(UpdateBeforeSimulationPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool UpdateBeforeSimulationPrefix(MyCubeBlock __instance)
        {
            var physics = __instance.CubeGrid?.Physics;
            return physics == null || physics.IsActive;
        }
    }
}
