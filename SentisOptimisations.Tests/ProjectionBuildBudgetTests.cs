using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests;

/// <summary>
/// The cap on projected blocks one player's welders materialize per simulation frame: it has to hold
/// the frame down without ever becoming the reason a projection is not built.
/// </summary>
public class ProjectionBuildBudgetTests
{
    private const long Player = 1;

    [Fact]
    public void Budget_is_per_simulation_frame()
    {
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.TryConsume(100, 1, 10, Player));
        Assert.False(budget.CanConsume(100, 1, 10, Player));
        Assert.False(budget.TryConsume(100, 1, 10, Player));
        Assert.True(budget.CanConsume(101, 1, 10, Player));
        Assert.True(budget.TryConsume(101, 1, 10, Player));
    }

    [Fact]
    public void Each_player_has_a_budget_of_their_own()
    {
        // One player's welders spend their frame; another player's welders build in the same frame.
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.TryConsume(100, 1, 10, 1));
        Assert.False(budget.TryConsume(100, 1, 20, 1));
        Assert.True(budget.TryConsume(100, 1, 30, 2));
        Assert.False(budget.TryConsume(100, 1, 40, 2));
        Assert.True(budget.TryConsume(100, 1, 50, 3));
        Assert.True(budget.TryConsume(101, 1, 20, 1));
    }

    [Fact]
    public void One_welder_cannot_take_the_whole_frame()
    {
        // Three builds are allowed this frame, but not all three by the same tool.
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.TryConsume(100, 3, 10, Player));
        Assert.False(budget.TryConsume(100, 3, 10, Player));
        Assert.True(budget.TryConsume(100, 3, 20, Player));
        Assert.True(budget.TryConsume(100, 3, 30, Player));
        Assert.False(budget.TryConsume(100, 3, 40, Player));
    }

    [Fact]
    public void A_welder_that_builds_nothing_holds_nobody_up()
    {
        // A ship carries welders that never reach the projection. Their asking must cost the
        // others nothing: whoever can build, builds.
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.CanConsume(100, 1, 10, Player));   // asks, finds nothing, does not consume
        Assert.True(budget.CanConsume(100, 1, 20, Player));
        Assert.True(budget.TryConsume(100, 1, 20, Player));
    }

    [Fact]
    public void Each_frame_starts_the_budget_again()
    {
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.TryConsume(100, 1, 10, Player));
        Assert.False(budget.TryConsume(100, 1, 20, Player));
        Assert.True(budget.TryConsume(101, 1, 20, Player));
        Assert.True(budget.TryConsume(102, 1, 10, Player));
    }

    [Fact]
    public void A_welder_that_was_quiet_for_a_while_is_served_at_once()
    {
        // Nothing is carried between frames, so a tool coming back after a pause does not wait for
        // a turn it lost while it was idle.
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.TryConsume(1, 1, 10, Player));
        for (var frame = 2; frame < 500; frame++) budget.TryConsume(frame, 1, 20, Player);
        Assert.True(budget.CanConsume(500, 1, 10, Player));
    }

    [Fact]
    public void Budget_supports_runtime_limit_changes()
    {
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.TryConsume(42, 3, 10, Player));
        Assert.True(budget.TryConsume(42, 3, 20, Player));
        Assert.True(budget.TryConsume(42, 3, 30, Player));
        Assert.False(budget.TryConsume(42, 3, 40, Player));
        Assert.True(budget.TryConsume(43, 1, 10, Player));
        Assert.False(budget.TryConsume(43, 1, 20, Player));
    }

    [Fact]
    public void A_limit_below_one_still_lets_one_block_through()
    {
        var budget = new ProjectionBuildBudget();
        Assert.True(budget.TryConsume(7, 0, 10, Player));
        Assert.False(budget.TryConsume(7, 0, 20, Player));
    }
}
