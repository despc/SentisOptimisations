using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests;

public class GcScheduleDeciderTests
{
    private const long Mb = 1024 * 1024;

    // Simulates natural collections: the heap grows by `budget` and then gen0 fires on its own.
    private static void LearnNaturalBudget(GcScheduleDecider decider, ref long gen0, long budget, int collections)
    {
        for (var i = 0; i < collections; i++)
        {
            decider.OnFrameEnd(gen0, heapBytes: 1000 * Mb + budget, workMs: 10);
            gen0++;
            decider.OnFrameEnd(gen0, heapBytes: 1000 * Mb, workMs: 10);
        }
    }

    [Fact]
    public void Does_not_collect_before_a_natural_budget_is_known()
    {
        var decider = new GcScheduleDecider();
        Assert.False(decider.OnFrameEnd(0, 1000 * Mb, workMs: 1));
        Assert.False(decider.OnFrameEnd(0, 1400 * Mb, workMs: 1));
    }

    [Fact]
    public void Collects_in_a_light_frame_once_most_of_the_budget_is_used()
    {
        var decider = new GcScheduleDecider();
        long gen0 = 0;
        LearnNaturalBudget(decider, ref gen0, 100 * Mb, 3);

        Assert.False(decider.OnFrameEnd(gen0, 1000 * Mb + 40 * Mb, workMs: 1));
        Assert.True(decider.OnFrameEnd(gen0, 1000 * Mb + 70 * Mb, workMs: 1));
    }

    [Fact]
    public void Never_collects_in_a_heavy_frame()
    {
        var decider = new GcScheduleDecider();
        long gen0 = 0;
        LearnNaturalBudget(decider, ref gen0, 100 * Mb, 3);

        Assert.False(decider.OnFrameEnd(gen0, 1000 * Mb + 90 * Mb, workMs: GcScheduleDecider.LightFrameMs + 0.1));
    }

    [Fact]
    public void Scheduled_collections_do_not_shrink_the_learned_budget()
    {
        var decider = new GcScheduleDecider();
        long gen0 = 0;
        LearnNaturalBudget(decider, ref gen0, 100 * Mb, 3);

        for (var i = 0; i < 20; i++)
        {
            Assert.True(decider.OnFrameEnd(gen0, 1000 * Mb + 70 * Mb, workMs: 1));
            decider.OnCollectedByScheduler();
            gen0++;
            decider.OnFrameEnd(gen0, 1000 * Mb, workMs: 1);
        }

        Assert.False(decider.OnFrameEnd(gen0, 1000 * Mb + 40 * Mb, workMs: 1));
        Assert.True(decider.OnFrameEnd(gen0, 1000 * Mb + 70 * Mb, workMs: 1));
    }
}
