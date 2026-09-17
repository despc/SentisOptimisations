using System.Linq;
using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests;

public class ProductionTimerPhaseTests
{
    [Fact]
    public void Phase_is_a_multiple_of_the_step_below_the_period()
    {
        for (ulong sequence = 0; sequence < 1000; sequence++)
        {
            var phase = ProductionTimerPhase.InitialFrames(sequence, periodFrames: 60, stepFrames: 10);
            Assert.True(phase < 60);
            Assert.Equal(0u, phase % 10);
        }
    }

    [Fact]
    public void Sequential_blocks_spread_evenly_over_every_phase()
    {
        var counts = Enumerable.Range(0, 6000)
            .Select(i => ProductionTimerPhase.InitialFrames((ulong)i, 60, 10))
            .GroupBy(p => p)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(6, counts.Count);
        Assert.All(counts.Values, c => Assert.InRange(c, 850, 1150));
    }

    [Fact]
    public void Phase_is_not_correlated_with_ten_update_buckets()
    {
        // Update10 buckets are also assigned in creation order; every bucket must still see all phases.
        for (var bucket = 0; bucket < 10; bucket++)
        {
            var phases = Enumerable.Range(0, 6000).Where(i => i % 10 == bucket)
                .Select(i => ProductionTimerPhase.InitialFrames((ulong)i, 60, 10))
                .Distinct().Count();
            Assert.Equal(6, phases);
        }
    }

    [Theory]
    [InlineData(0u, 10u)]
    [InlineData(10u, 10u)]
    [InlineData(60u, 0u)]
    public void Degenerate_timers_keep_phase_zero(uint period, uint step)
    {
        Assert.Equal(0u, ProductionTimerPhase.InitialFrames(12345, period, step));
    }
}
