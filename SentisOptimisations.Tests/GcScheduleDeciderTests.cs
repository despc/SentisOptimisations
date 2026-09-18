using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests;

public class GcScheduleDeciderTests
{
    private const long Mb = 1024 * 1024;

    // Simulates natural collections: allocations grow by `budget` and then gen0 fires on its own.
    // Frames run at 10 ms so the median settles; workMs stays below budget + pause estimate.
    private static void LearnNaturalBudget(GcScheduleDecider decider, ref long gen0, long budget, int collections)
    {
        for (var i = 0; i < collections; i++)
        {
            decider.OnFrameEnd(gen0, allocatedBytes: 1000 * Mb + budget, workMs: 10);
            gen0++;
            decider.OnFrameEnd(gen0, allocatedBytes: 1000 * Mb, workMs: 10);
        }
        // The decider waits until it has seen enough frames to trust the median; steady frames
        // with no allocation growth warm it without collecting.
        for (var i = 0; i < 40; i++)
            decider.OnFrameEnd(gen0, allocatedBytes: 1000 * Mb, workMs: 10);
    }

    [Fact]
    public void Does_not_collect_before_a_natural_budget_is_known()
    {
        var decider = new GcScheduleDecider();
        Assert.False(decider.OnFrameEnd(0, 1000 * Mb, workMs: 1));
        Assert.False(decider.OnFrameEnd(0, 1400 * Mb, workMs: 1));
    }

    [Fact]
    public void Collects_once_most_of_the_budget_is_used_in_a_mediocre_frame()
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
        long gen0 = 0;
        // A single natural collection learns the budget but leaves the window below warmup.
        LearnNaturalBudget(decider, ref gen0, 100 * Mb, 1);
        Assert.False(decider.OnFrameEnd(gen0, 1000 * Mb + 90 * Mb, workMs: 1));
    }

    [Fact]
    public void Never_collects_in_a_frame_the_pause_does_not_fit_into()
    {
        var decider = new GcScheduleDecider();
        long gen0 = 0;
        LearnNaturalBudget(decider, ref gen0, 100 * Mb, 3);

        // 13 ms of work + a 4 ms estimated pause exceeds the 16.7 ms budget: not here.
        Assert.False(decider.OnFrameEnd(gen0, 1000 * Mb + 90 * Mb, workMs: 13));
    }

    [Fact]
    public void Collects_in_a_frame_below_the_median_even_above_the_old_constant_threshold()
    {
        var decider = new GcScheduleDecider();
        long gen0 = 0;
        // Learn the median around 12 ms of frame work.
        for (var i = 0; i < 40; i++) decider.OnFrameEnd(0, allocatedBytes: 1000 * Mb, workMs: 12);
        ref var g = ref gen0;
        LearnNaturalBudget(decider, ref g, 100 * Mb, 1);
        for (var i = 0; i < 40; i++) decider.OnFrameEnd(gen0, allocatedBytes: 1000 * Mb, workMs: 12);

        // 5 ms is far above the old 4 ms constant but below the 12 ms median and the pause fits.
        Assert.True(decider.OnFrameEnd(gen0, 1000 * Mb + 70 * Mb, workMs: 5));
    }

    [Fact]
    public void Pause_estimate_follows_real_pauses()
    {
        var decider = new GcScheduleDecider();
        long gen0 = 0;
        LearnNaturalBudget(decider, ref gen0, 100 * Mb, 3);

        // After observing 10 ms pauses, a frame with 10 ms of work no longer fits (10+~9 > 16.7).
        for (var i = 0; i < 10; i++) decider.OnCollectedByScheduler(10);
        Assert.False(decider.OnFrameEnd(gen0, 1000 * Mb + 70 * Mb, workMs: 10));

        // After observing small pauses again - and the minimum interval passing - the same frame
        // fits again; a collection resets the growth baseline, so the heap has to fill back past
        // 60% of the budget.
        for (var i = 0; i < 20; i++) decider.OnCollectedByScheduler(1);
        for (var f = 0; f < GcScheduleDecider.MinIntervalFrames; f++)
            decider.OnFrameEnd(gen0, allocatedBytes: 1000 * Mb, workMs: 10);
        Assert.False(decider.OnFrameEnd(gen0, 1000 * Mb + 100 * Mb, workMs: 10));
        Assert.True(decider.OnFrameEnd(gen0, 1000 * Mb + 170 * Mb, workMs: 10));
    }

    [Fact]
    public void Stands_down_when_collections_are_inherently_expensive()
    {
        var decider = new GcScheduleDecider();
        long gen0 = 0;
        LearnNaturalBudget(decider, ref gen0, 100 * Mb, 3);

        // Observed pauses above the ceiling: even an otherwise perfect frame does not collect.
        for (var i = 0; i < 10; i++) decider.OnCollectedByScheduler(20);
        for (var f = 0; f < GcScheduleDecider.MinIntervalFrames; f++)
            decider.OnFrameEnd(gen0, allocatedBytes: 1000 * Mb, workMs: 1);
        Assert.False(decider.OnFrameEnd(gen0, 1000 * Mb + 70 * Mb, workMs: 1));
    }

    [Fact]
    public void Pause_estimate_recovers_when_the_heap_goes_quiet()
    {
        var decider = new GcScheduleDecider();
        long gen0 = 0;
        LearnNaturalBudget(decider, ref gen0, 100 * Mb, 3);
        for (var i = 0; i < 10; i++) decider.OnCollectedByScheduler(20);

        // A long spell with no natural gen0 (heap quiet, estimate decaying) must bring the
        // estimate back under the ceiling so the scheduler resumes.
        for (var f = 0; f < 1500; f++)
            decider.OnFrameEnd(gen0, allocatedBytes: 1000 * Mb, workMs: 1);
        for (var f = 0; f < GcScheduleDecider.MinIntervalFrames; f++)
            decider.OnFrameEnd(gen0, allocatedBytes: 1000 * Mb, workMs: 1);
        Assert.True(decider.OnFrameEnd(gen0, 1000 * Mb + 70 * Mb, workMs: 1));
    }

    [Fact]
    public void Scheduled_collections_do_not_shrink_the_learned_budget()
    {
        var decider = new GcScheduleDecider();
        long gen0 = 0;
        LearnNaturalBudget(decider, ref gen0, 100 * Mb, 3);

        for (var i = 0; i < 20; i++)
        {
            // Respect the minimum interval between scheduled collections.
            for (var f = 0; f < GcScheduleDecider.MinIntervalFrames; f++)
                decider.OnFrameEnd(gen0, allocatedBytes: 1000 * Mb, workMs: 1);
            Assert.True(decider.OnFrameEnd(gen0, 1000 * Mb + 70 * Mb, workMs: 1));
            decider.OnCollectedByScheduler(2);
            gen0++;
            decider.OnFrameEnd(gen0, allocatedBytes: 1000 * Mb, workMs: 1);
        }

        Assert.False(decider.OnFrameEnd(gen0, 1000 * Mb + 40 * Mb, workMs: 1));
        for (var f = 0; f < GcScheduleDecider.MinIntervalFrames; f++)
            decider.OnFrameEnd(gen0, allocatedBytes: 1000 * Mb, workMs: 1);
        Assert.True(decider.OnFrameEnd(gen0, 1000 * Mb + 70 * Mb, workMs: 1));
    }
}
