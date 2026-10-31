using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class WelderIdleGateTests
    {
        [Fact]
        public void An_idle_welder_waits_the_whole_interval()
        {
            var next = WelderOptimization.WelderIdleGate.NextFrameAfter(false, 1000, 5);
            Assert.Equal(1300, next); // 5 seconds at 60 frames
            Assert.True(WelderOptimization.WelderIdleGate.ShouldSkip(next, 1001));
            Assert.True(WelderOptimization.WelderIdleGate.ShouldSkip(next, 1299));
            Assert.False(WelderOptimization.WelderIdleGate.ShouldSkip(next, 1300));
        }

        [Fact]
        public void A_welder_with_work_is_never_slowed_down()
        {
            var next = WelderOptimization.WelderIdleGate.NextFrameAfter(true, 1000, 5);
            Assert.Equal(0, next);
            Assert.False(WelderOptimization.WelderIdleGate.ShouldSkip(next, 1001));
        }

        [Fact]
        public void Zero_interval_disables_the_slowdown()
        {
            var next = WelderOptimization.WelderIdleGate.NextFrameAfter(false, 1000, 0);
            Assert.Equal(0, next);
            Assert.False(WelderOptimization.WelderIdleGate.ShouldSkip(next, 1001));
        }
    }
}
