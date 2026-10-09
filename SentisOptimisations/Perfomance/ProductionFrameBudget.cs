using System;
using System.Reflection;
using Sandbox.Game.Components;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using VRage.Game.Components;
using VRage.Game.ObjectBuilders.ComponentSystem;
using SentisOptimisationsPlugin;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// A refinery or an assembler does not do its round in a frame that is already heavy; it does it a little later, for
    /// the whole time since its last round.
    ///
    /// A production block works from a timer: every 10 frames the timer adds 10 to its count, and when the count reaches
    /// the block's period (60 frames and more, by presence tier) the block does its round - pulls from the conveyors,
    /// refines or assembles for the frames counted (<c>framesFromLastTrigger * 16 ms</c>), pushes the result on - and the
    /// count starts again from 0. Here, when the frame has already done <see cref="BusyMs"/> of simulation by the time a
    /// block's round comes, the count goes on and the round waits for the block's next 10-frame update, up to
    /// <see cref="MaxStretch"/> times its period. The round that follows counts all the frames, so nothing is lost: the
    /// same ore is refined, only in fewer, larger rounds when the server is loaded, and not in the frames that are over
    /// already. The game does the same by itself for grids far from players (the presence tiers make the period longer).
    /// Only a block with something queued waits: an idle one's round is what brings it work, and a long round that finds
    /// the queue empty would lose all its frames.
    /// </summary>
    [PatchShim]
    public static class ProductionFrameBudget
    {
        /// <summary>Simulation already done in the frame, ms, from which a round waits.</summary>
        public const double BusyMs = 12;

        /// <summary>A round waits at most until the count reaches this many periods.</summary>
        public const uint MaxStretch = 4;

        private static Func<MyTimerComponent, bool> _forceTrigger;

        // statistics, for the stand
        public static long Waited;

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("ProductionFrameBudget", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _forceTrigger = Accessors.Field<MyTimerComponent, bool>("m_forceTrigger");
            var update = typeof(MyTimerComponent).GetMethod("Update", any, null, new[] { typeof(bool) }, null)
                         ?? throw new MissingMethodException("MyTimerComponent", "Update");
            ctx.GetPattern(update).Prefixes.Add(typeof(ProductionFrameBudget).GetMethod(nameof(UpdatePrefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>
        /// Whether a round that is due now waits: the frame is busy and the count, with this update's frames, has not yet
        /// reached <see cref="MaxStretch"/> periods.
        /// </summary>
        public static bool Wait(uint counted, uint step, uint period, double frameMs) =>
            period > 0 && counted + step >= period && counted + step < period * MaxStretch && frameMs >= BusyMs;

        private static bool UpdatePrefix(MyTimerComponent __instance, bool forceUpdate)
        {
            try
            {
                if (forceUpdate || !__instance.TimerEnabled || !__instance.Repeat) return true;
                // a block with nothing queued does its round on time: the round is what brings it work, and a long
                // round that finds the queue empty loses all its frames (refinery_perf_busy: the first 10 s refined
                // 0.3% instead of 8.8%, as every refinery waited four periods to take its first ore)
                if (!(__instance.Container?.Entity is MyProductionBlock block) || block.IsQueueEmpty) return true;
                uint step;
                switch (__instance.TimerType)
                {
                    case MyTimerTypes.Frame10: step = 10; break;
                    case MyTimerTypes.Frame100: step = 100; break;
                    default: return true;
                }
                if (_forceTrigger(__instance)) return true;
                if (!Wait(__instance.FramesFromLastTrigger, step, __instance.TimerTickInFrames, FrameClock.ElapsedMs)) return true;
                // the vanilla count, without the round
                __instance.FramesFromLastTrigger += step;
                System.Threading.Interlocked.Increment(ref Waited);
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
