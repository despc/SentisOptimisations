using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests;

public class GcScheduleDeciderTests
{
    private const long Mb = 1024 * 1024;

    // Warms the median with ordinary frames of `frameMs`, then simulates natural collections:
    // allocations grow by `budget` and gen0 fires on its own in a frame that is longer than usual
    // by `naturalPauseMs`, the way a real collection shows up in the frame it lands in.
    private static void LearnNaturalBudget(GcScheduleDecider decider, ref long gen0, long budget, int collections,
        double frameMs = 10, double naturalPauseMs = GcScheduleDecider.InitialPauseEstimateMs)
    {
        for (var i = 0; i < 40; i++)
            decider.OnFrameEnd(gen0, allocatedBytes: 1000 * Mb, workMs: frameMs);
        for (var i = 0; i < collections; i++)
            NaturalCollection(decider, ref gen0, budget, frameMs, naturalPauseMs);
    }

    private static void NaturalCollection(GcScheduleDecider decider, ref long gen0, long budget, double frameMs, double pauseMs)
    {
        decider.OnFrameEnd(gen0, allocatedBytes: 1000 * Mb + budget, workMs: frameMs);
        gen0++;
        decider.OnFrameEnd(gen0, allocatedBytes: 1000 * Mb, workMs: frameMs + pauseMs);
    }

    [Fact]
    public void Does_not_collect_before_a_natural_budget_is_known()
    {
        var decider = new GcScheduleDecider();
        Assert.False(decider.OnFrameEnd(0, 1000 * Mb, workMs: 1));
        Assert.False(decider.OnFrameEnd(0, 1400 * Mb, workMs: 1));
    }

    [Fact]
    public void Collects_once_most_of_the_budget_is_used_in_a_light_frame()
    {
        var decider = new GcScheduleDecider();
        long gen0 = 0;
        LearnNaturalBudget(decider, ref gen0, 100 * Mb, 3);

        Assert.False(decider.OnFrameEnd(gen0, 1000 * Mb + 40 * Mb, workMs: 1));
        Assert.True(decider.OnFrameEnd(gen0, 1000 * Mb + 70 * Mb, workMs: 1));
    }

    [Fact]
    public void Does_not_collect_while_the_median_window_is_cold()
    {
        var decider = new GcScheduleDecider();
        // The budget is learned from one natural collection, but there are too few ordinary
        // frames to trust their median yet.
        decider.OnFrameEnd(0, 1000 * Mb, workMs: 10);
        decider.OnFrameEnd(0, 1100 * Mb, workMs: 10);
        decider.OnFrameEnd(1, 1000 * Mb, workMs: 14);
        Assert.False(decider.OnFrameEnd(1, 1000 * Mb + 90 * Mb, workMs: 1));
    }

    [Fact]
    public void Never_collects_in_a_frame_the_pause_does_not_fit_into()
    {
        var decider = new GcScheduleDecider();
        long gen0 = 0;
        LearnNaturalBudget(decider, ref gen0, 100 * Mb, 3, frameMs: 14);

        // Below the 14 ms median, but 13 ms of work and a 4 ms pause exceed the 16.7 ms budget.
        Assert.False(decider.OnFrameEnd(gen0, 1000 * Mb + 90 * Mb, workMs: 13));
    }

    [Fact]
    public void Never_collects_in_a_frame_heavier_than_the_median()
    {
        var decider = new GcScheduleDecider();
        long gen0 = 0;
        LearnNaturalBudget(decider, ref gen0, 100 * Mb, 3, frameMs: 5);

        // The pause would fit, but the frame is heavier than usual: a lighter one will come.
        Assert.False(decider.OnFrameEnd(gen0, 1000 * Mb + 90 * Mb, workMs: 8));
    }

    [Fact]
    public void Collects_in_a_frame_below_the_median_even_above_the_old_constant_threshold()
    {
        var decider = new GcScheduleDecider();
        long gen0 = 0;
        LearnNaturalBudget(decider, ref gen0, 100 * Mb, 3, frameMs: 12);

        // 5 ms is above the old 4 ms constant but below the 12 ms median, and the pause fits.
        Assert.True(decider.OnFrameEnd(gen0, 1000 * Mb + 70 * Mb, workMs: 5));
    }

    [Fact]
    public void Pause_estimate_follows_our_own_pauses()
    {
        var decider = new GcScheduleDecider();
        long gen0 = 0;
        LearnNaturalBudget(decider, ref gen0, 100 * Mb, 3);

        // After 10 ms pauses, a frame with 10 ms of work no longer fits (10 + ~10 > 16.7).
        for (var i = 0; i < 10; i++) decider.OnCollectedByScheduler(10, gen0);
        Assert.False(decider.OnFrameEnd(gen0, 1000 * Mb + 70 * Mb, workMs: 10));

        // After small pauses again - and the minimum interval passing - the same frame fits; a
        // collection resets the growth baseline, so the heap has to fill back past 60% of the budget.
        for (var i = 0; i < 20; i++) decider.OnCollectedByScheduler(1, gen0);
        for (var f = 0; f < GcScheduleDecider.MinIntervalFrames; f++)
            decider.OnFrameEnd(gen0, allocatedBytes: 1000 * Mb, workMs: 10);
        Assert.False(decider.OnFrameEnd(gen0, 1000 * Mb + 100 * Mb, workMs: 10));
        Assert.True(decider.OnFrameEnd(gen0, 1000 * Mb + 170 * Mb, workMs: 10));
    }

    [Fact]
    public void Pause_estimate_follows_natural_collections_without_forcing_any()
    {
        var decider = new GcScheduleDecider();
        long gen0 = 0;
        LearnNaturalBudget(decider, ref gen0, 100 * Mb, 3);

        // Load grows: natural collections now make their frames 15 ms longer. The scheduler learns
        // that from them alone and stops fitting the pause into ordinary 10 ms frames.
        for (var i = 0; i < 10; i++) NaturalCollection(decider, ref gen0, 100 * Mb, 10, 15);
        Assert.InRange(decider.PauseEstimateMs, 14, 15.5);
        Assert.False(decider.OnFrameEnd(gen0, 1000 * Mb + 70 * Mb, workMs: 10));

        // Load drops: natural collections get cheap, and the scheduler comes back by itself - no
        // probe collection is needed to find that out.
        for (var i = 0; i < 10; i++) NaturalCollection(decider, ref gen0, 100 * Mb, 10, 0.5);
        Assert.True(decider.OnFrameEnd(gen0, 1000 * Mb + 70 * Mb, workMs: 10));
    }

    [Fact]
    public void Scheduled_collections_are_not_taken_for_natural_ones()
    {
        var decider = new GcScheduleDecider();
        long gen0 = 0;
        LearnNaturalBudget(decider, ref gen0, 100 * Mb, 3);

        // The thread's allocation counter only ever grows.
        var allocated = 1000 * Mb;
        for (var i = 0; i < 20; i++)
        {
            // Respect the minimum interval between scheduled collections.
            for (var f = 0; f < GcScheduleDecider.MinIntervalFrames; f++)
                decider.OnFrameEnd(gen0, allocated, workMs: 1);
            allocated += 70 * Mb;
            Assert.True(decider.OnFrameEnd(gen0, allocated, workMs: 1));
            gen0++;
            decider.OnCollectedByScheduler(2, gen0);
            decider.OnFrameEnd(gen0, allocated, workMs: 1);
        }

        // Taken for natural ones, they would have taught a zero pause from the light frames after
        // them and shrunk the budget to the little growth before them.
        Assert.InRange(decider.PauseEstimateMs, 1.9, 2.1);
        Assert.False(decider.OnFrameEnd(gen0, allocated + 40 * Mb, workMs: 1));
        for (var f = 0; f < GcScheduleDecider.MinIntervalFrames; f++)
            decider.OnFrameEnd(gen0, allocated + 40 * Mb, workMs: 1);
        Assert.True(decider.OnFrameEnd(gen0, allocated + 70 * Mb, workMs: 1));
    }
}
