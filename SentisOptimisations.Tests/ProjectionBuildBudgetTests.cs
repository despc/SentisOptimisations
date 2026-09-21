using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests;

/// <summary>
/// The cap on projected blocks materialized per simulation frame: it has to hold the frame down
/// without ever becoming the reason a projection is not built.
/// </summary>
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
    public void One_welder_cannot_take_the_whole_frame()
    {
        // Three builds are allowed this frame, but not all three by the same tool.
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.TryConsume(100, 3, 10));
        Assert.False(budget.TryConsume(100, 3, 10));
        Assert.True(budget.TryConsume(100, 3, 20));
        Assert.True(budget.TryConsume(100, 3, 30));
        Assert.False(budget.TryConsume(100, 3, 40));
    }

    [Fact]
    public void A_welder_that_builds_nothing_holds_nobody_up()
    {
        // A ship carries welders that never reach the projection. Their asking must cost the
        // others nothing: whoever can build, builds.
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.CanConsume(100, 1, 10));   // asks, finds nothing, does not consume
        Assert.True(budget.CanConsume(100, 1, 20));
        Assert.True(budget.TryConsume(100, 1, 20));
    }

    [Fact]
    public void Each_frame_starts_the_budget_again()
    {
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.TryConsume(100, 1, 10));
        Assert.False(budget.TryConsume(100, 1, 20));
        Assert.True(budget.TryConsume(101, 1, 20));
        Assert.True(budget.TryConsume(102, 1, 10));
    }

    [Fact]
    public void A_welder_that_was_quiet_for_a_while_is_served_at_once()
    {
        // Nothing is carried between frames, so a tool coming back after a pause does not wait for
        // a turn it lost while it was idle.
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.TryConsume(1, 1, 10));
        for (var frame = 2; frame < 500; frame++) budget.TryConsume(frame, 1, 20);
        Assert.True(budget.CanConsume(500, 1, 10));
    }

    [Fact]
    public void Budget_supports_runtime_limit_changes()
    {
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.TryConsume(42, 3, 10));
        Assert.True(budget.TryConsume(42, 3, 20));
        Assert.True(budget.TryConsume(42, 3, 30));
        Assert.False(budget.TryConsume(42, 3, 40));
        Assert.True(budget.TryConsume(43, 1, 10));
        Assert.False(budget.TryConsume(43, 1, 20));
    }

    [Fact]
    public void A_limit_below_one_still_lets_one_block_through()
    {
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.TryConsume(7, 0, 10));
        Assert.False(budget.TryConsume(7, 0, 20));
    }
}
