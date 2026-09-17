using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests;

public class ProjectionBuildBudgetTests
{
    [Fact]
    public void Budget_is_global_per_simulation_frame()
    {
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.TryConsume(100, 1, 10));
        Assert.False(budget.CanConsume(100, 1, 10));
        Assert.False(budget.TryConsume(100, 1, 10));
        Assert.True(budget.CanConsume(101, 1, 10));
        Assert.True(budget.TryConsume(101, 1, 10));
    }

    [Fact]
    public void Active_welders_receive_round_robin_turns()
    {
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.TryConsume(100, 1, 10));
        Assert.False(budget.TryConsume(100, 1, 20)); // registers owner 20
        Assert.False(budget.TryConsume(101, 1, 10));
        Assert.True(budget.TryConsume(101, 1, 20));
        Assert.True(budget.TryConsume(102, 1, 10));
    }

    [Fact]
    public void Stale_owner_cannot_block_live_welder_forever()
    {
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.TryConsume(1, 1, 10));
        Assert.False(budget.TryConsume(1, 1, 20));
        Assert.True(budget.TryConsume(2, 1, 20));
        Assert.True(budget.TryConsume(200, 1, 20));
    }

    [Fact]
    public void Budget_supports_runtime_limit_changes()
    {
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.TryConsume(42, 3, 10));
        Assert.True(budget.TryConsume(42, 3, 10));
        Assert.True(budget.TryConsume(42, 3, 10));
        Assert.False(budget.TryConsume(42, 3, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_limit_is_normalized_to_one(int limit)
    {
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.TryConsume(7, limit, 10));
        Assert.False(budget.TryConsume(7, limit, 10));
    }
}
