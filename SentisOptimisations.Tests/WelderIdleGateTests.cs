using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class WelderIdleGateTests
    {
        private static WelderOptimization.WelderIdleState Advance(WelderOptimization.WelderIdleState state,
            long welderId, bool hadWork, long frame, int idleSeconds = 3, int idleActivations = 4)
        {
            WelderOptimization.WelderIdleGate.Advance(ref state, welderId, hadWork, frame, idleSeconds, idleActivations);
            return state;
        }

        [Fact]
        public void Slow_mode_engages_only_after_N_consecutive_empty_activations()
        {
            var s = default(WelderOptimization.WelderIdleState);
            for (var i = 1; i <= 3; i++)
            {
                s = Advance(s, 42, false, 1000 + i * 15);
                Assert.Equal(0, s.NextFrame); // below the threshold: still full speed
                Assert.False(s.Slow);
            }
            s = Advance(s, 42, false, 1060);
            Assert.True(s.Slow);
            Assert.Equal(4, s.EmptyActivations);
            var interval = 3 * 60L;
            var phase = (42L * 2654435761L) % interval;
            Assert.Equal(1060 + interval + phase, s.NextFrame);
            Assert.True(WelderOptimization.WelderIdleGate.ShouldSkip(s.NextFrame, s.NextFrame - 1));
            Assert.False(WelderOptimization.WelderIdleGate.ShouldSkip(s.NextFrame, s.NextFrame));
        }

        [Fact]
        public void Any_real_work_leaves_the_slow_mode_immediately_and_resets_the_counter()
        {
            var s = default(WelderOptimization.WelderIdleState);
            for (var i = 0; i < 6; i++) s = Advance(s, 42, false, 1000 + i * 15);
            Assert.True(s.Slow);
            s = Advance(s, 42, true, 2000); // one activation with welding or a projection build
            Assert.Equal(0, s.NextFrame);
            Assert.Equal(0, s.EmptyActivations);
            Assert.False(s.Slow);
            // ...and the next empty activations have to earn the slowdown again.
            s = Advance(s, 42, false, 2015);
            Assert.Equal(0, s.NextFrame);
            Assert.False(s.Slow);
        }

        [Fact]
        public void Work_before_the_threshold_keeps_the_welder_at_full_speed()
        {
            var s = default(WelderOptimization.WelderIdleState);
            s = Advance(s, 42, false, 1000);
            s = Advance(s, 42, false, 1015);
            s = Advance(s, 42, true, 1030);  // work resets the streak before N is reached
            s = Advance(s, 42, false, 1045);
            s = Advance(s, 42, false, 1060);
            Assert.Equal(2, s.EmptyActivations);
            Assert.Equal(0, s.NextFrame);
        }

        [Fact]
        public void Slow_activations_of_different_welders_are_staggered_over_frames()
        {
            var interval = 3 * 60L;
            var phases = new System.Collections.Generic.HashSet<long>();
            for (long id = 1; id <= 6; id++)
            {
                var s = default(WelderOptimization.WelderIdleState);
                for (var i = 0; i < 4; i++) s = Advance(s, id, false, 1000 + i * 15, 3, 4);
                Assert.True(s.Slow);
                phases.Add(s.NextFrame % interval);
                // Once slow, the cadence is exactly the interval - the phase is applied only on entry.
                var next = Advance(s, id, false, s.NextFrame, 3, 4);
                Assert.Equal(s.NextFrame + interval, next.NextFrame);
            }
            Assert.Equal(6, phases.Count); // ids 1..6 land on six different frames
        }

        [Fact]
        public void Zero_interval_disables_the_slowdown()
        {
            var s = default(WelderOptimization.WelderIdleState);
            for (var i = 0; i < 10; i++) s = Advance(s, 42, false, 1000 + i * 15, 0, 4);
            Assert.Equal(0, s.NextFrame);
            Assert.False(WelderOptimization.WelderIdleGate.ShouldSkip(s.NextFrame, 5000));
        }
    }
}
