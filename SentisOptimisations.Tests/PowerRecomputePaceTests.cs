using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests;

public class PowerRecomputePaceTests
{
    [Fact]
    public void A_cheap_recompute_does_not_wait()
    {
        Assert.Equal(0, PowerRecomputePace.PauseMs(0.3, 8));
    }

    [Fact]
    public void An_expensive_one_waits_its_cost_times_the_factor()
    {
        Assert.Equal(480, PowerRecomputePace.PauseMs(60, 8));
        Assert.Equal(16, PowerRecomputePace.PauseMs(2, 8));
    }

    [Fact]
    public void The_wait_is_capped_and_off_with_a_factor_of_zero()
    {
        Assert.Equal(PowerRecomputePace.MaxPauseMs, PowerRecomputePace.PauseMs(400, 8));
        Assert.Equal(0, PowerRecomputePace.PauseMs(60, 0));
    }
}
