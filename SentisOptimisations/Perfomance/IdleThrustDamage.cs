using System.Reflection;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using VRage.Utils;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// A thruster that gives no thrust looks for nothing to burn.
    ///
    /// Every working thruster, every 100 frames, casts a capsule along each of its flames for what the flame
    /// burns (MyThrust.ThrustDamageAsync) - the idle ones too, the game's MyFakes.INACTIVE_THRUSTER_DMG being
    /// on. A ship at rest with dampeners has every thruster idle; 128 Spitfires (191 thrusters each) paid
    /// 3.2 ms a frame for it on the stand, 8.4 us a thruster. An idle thruster no longer burns what stands at
    /// its nozzle; one that gives thrust burns as before, over a flame length rolled right before.
    /// </summary>
    [PatchShim]
    public static class IdleThrustDamage
    {
        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("IdleThrustDamage", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var damage = typeof(MyThrust).GetMethod("ThrustDamageAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            ctx.GetPattern(damage).Prefixes.Add(typeof(IdleThrustDamage).GetMethod(nameof(ThrustDamageAsyncPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>
        /// Skips an idle thruster. One that gives thrust gets its flame length first: the game only
        /// rolls it in the thruster's ten-frame update, which a server turns off once the thruster has
        /// nothing to draw, and a length left from an idle moment would burn nothing - or burn at full
        /// length after the thrust is gone. The roll is the game's own (UpdateThrusterLenght); this
        /// runs on the game thread, from the thruster's timer.
        /// </summary>
        private static bool ThrustDamageAsyncPrefix(MyThrust __instance)
        {
            var strength = __instance.CurrentStrength;
            if (strength <= 0f) return false;
            __instance.ThrustLengthRand = strength * 10f * MyUtils.GetRandomFloat(0.6f, 1f) *
                                          __instance.BlockDefinition.FlameLengthScale;
            return true;
        }
    }
}
