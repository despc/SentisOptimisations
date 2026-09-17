using System.Linq;
using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests;

public class PullFrameBudgetTests
{
    [Fact]
    public void Low_demand_always_passes()
    {
        var budget = new PullFrameBudget();
        for (long frame = 0; frame < 3; frame++)
        for (var i = 0; i < 10; i++)
            Assert.True(budget.TryAcquire(frame, limit: 30, starving: false, fill: 0.5f));
    }

    [Fact]
    public void High_demand_serves_the_emptiest_first_about_the_limit()
    {
        var budget = new PullFrameBudget();
        var fills = Enumerable.Range(0, 120).Select(i => 0.1f + i * 0.004f).ToArray();
        foreach (var fill in fills) budget.TryAcquire(0, 30, false, fill);

        var passed = fills.Where(fill => budget.TryAcquire(1, 30, false, fill)).ToArray();
        Assert.Equal(fills.Take(30), passed);
    }

    [Fact]
    public void Starving_pulls_always_pass_and_ceiling_bounds_a_frame()
    {
        var budget = new PullFrameBudget();
        for (var i = 0; i < 200; i++) budget.TryAcquire(0, 1, false, 0f);
        Assert.True(budget.TryAcquire(1, 1, starving: true, fill: 0.9f));
        Assert.True(budget.TryAcquire(1, 1, false, 0f));
        Assert.False(budget.TryAcquire(1, 1, false, 0f));
    }
}
