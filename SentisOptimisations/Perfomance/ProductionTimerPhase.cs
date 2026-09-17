using System;
using System.Reflection;
using System.Threading;
using Sandbox.Game.Components;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.ObjectBuilders.ComponentSystem;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Spreads production-block update timers over their whole period.
    ///
    /// Vanilla production blocks run their work (conveyor pulls, refining, assembling) from a
    /// Frame10 timer with a 60-frame period: every UpdateAfterSimulation10 adds 10 frames and the
    /// timer fires at 60. Update10 itself is spread over 10 frame buckets, but every block created
    /// together (grid spawn, world load, paste) starts its counter at the same value, so a whole
    /// bucket fires in one frame and stays idle for the next five passes. With thousands of
    /// refineries that is ~1/10 of all production work in a single frame (measured: 670 refinery
    /// ticks, 50-77 ms). Giving each timer a pseudo-random initial phase spreads the same work over
    /// all six passes. The total amount of work and production does not change; a block's first
    /// trigger just comes up to 50 frames earlier once.
    /// </summary>
    [PatchShim]
    public static class ProductionTimerPhase
    {
        private static readonly FieldInfo TimerField =
            typeof(MyFunctionalBlock).GetField("m_timer", BindingFlags.Instance | BindingFlags.NonPublic);

        private static long _sequence;

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("ProductionTimerPhase", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var createTimer = typeof(MyFunctionalBlock).GetMethod(nameof(MyFunctionalBlock.CreateUpdateTimer),
                BindingFlags.Instance | BindingFlags.Public);
            ctx.GetPattern(createTimer).Suffixes.Add(
                typeof(ProductionTimerPhase).GetMethod(nameof(CreateUpdateTimerSuffix),
                    BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static void CreateUpdateTimerSuffix(MyFunctionalBlock __instance)
        {
            try
            {
                if (!(__instance is MyProductionBlock)) return;
                var timer = TimerField?.GetValue(__instance) as MyTimerComponent;
                if (timer == null || timer.TimerType != MyTimerTypes.Frame10) return;

                var sequence = (ulong)Interlocked.Increment(ref _sequence);
                timer.FramesFromLastTrigger = InitialFrames(sequence, timer.TimerTickInFrames, 10);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "ProductionTimerPhase failed");
            }
        }

        /// <summary>
        /// Initial frame counter for the <paramref name="sequence"/>-th timer: a multiple of
        /// <paramref name="stepFrames"/> below <paramref name="periodFrames"/>. The sequence goes
        /// through SplitMix64 so that phases do not correlate with the creation-order assignment of
        /// Update10 buckets.
        /// </summary>
        public static uint InitialFrames(ulong sequence, uint periodFrames, uint stepFrames)
        {
            if (stepFrames == 0 || periodFrames <= stepFrames) return 0;
            var slots = periodFrames / stepFrames;
            var z = sequence + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (uint)(z % slots) * stepFrames;
        }
    }
}
