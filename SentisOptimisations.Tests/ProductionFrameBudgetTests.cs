using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class ProductionFrameBudgetTests
    {
        [Fact]
        public void A_round_that_is_not_due_is_left_to_the_game()
        {
            Assert.False(ProductionFrameBudget.Wait(40, 10, 60, 20));
        }

        [Fact]
        public void A_due_round_waits_only_in_a_busy_frame()
        {
            Assert.True(ProductionFrameBudget.Wait(50, 10, 60, ProductionFrameBudget.BusyMs + 1));
            Assert.False(ProductionFrameBudget.Wait(50, 10, 60, ProductionFrameBudget.BusyMs - 1));
        }

        [Fact]
        public void A_round_waits_at_most_so_many_periods()
        {
            Assert.True(ProductionFrameBudget.Wait(60 * ProductionFrameBudget.MaxStretch - 20, 10, 60, 30));
            Assert.False(ProductionFrameBudget.Wait(60 * ProductionFrameBudget.MaxStretch - 10, 10, 60, 30));
        }

        [Fact]
        public void A_paused_timer_never_waits()
        {
            Assert.False(ProductionFrameBudget.Wait(1000, 10, 0, 30));
        }

        [Fact]
        public void A_budget_takes_no_more_than_the_frame_has_left()
        {
            Assert.Equal(4, FrameClock.Allowed(4, 0.2, 5));
            Assert.Equal(1, FrameClock.Allowed(4, 0.2, FrameClock.TargetMs - 1), 6);
            Assert.Equal(0.2, FrameClock.Allowed(4, 0.2, 20));
        }

        [Fact]
        public void A_frame_is_a_spike_past_three_usual_frames_and_33_ms()
        {
            Assert.False(FrameClock.IsSpike(30, 5));
            Assert.True(FrameClock.IsSpike(40, 5));
            Assert.False(FrameClock.IsSpike(40, 15));
            Assert.True(FrameClock.IsSpike(50, 15));
            Assert.True(FrameClock.IsSpike(40, 0));
        }
    }
}
