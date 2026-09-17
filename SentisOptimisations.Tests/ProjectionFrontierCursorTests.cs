using System.Linq;
using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests;

public class ProjectionFrontierCursorTests
{
    [Fact]
    public void Scan_is_bounded_and_resumes_from_previous_cursor()
    {
        var cursor = new ProjectionFrontierCursor();

        Assert.Equal(new[] { 0, 1, 2 }, cursor.Take(10, 3).ToArray());
        Assert.Equal(new[] { 3, 4, 5 }, cursor.Take(10, 3).ToArray());
    }

    [Fact]
    public void Scan_wraps_without_exceeding_budget()
    {
        var cursor = new ProjectionFrontierCursor();

        Assert.Equal(new[] { 0, 1, 2, 3 }, cursor.Take(5, 4).ToArray());
        Assert.Equal(new[] { 4, 0, 1, 2 }, cursor.Take(5, 4).ToArray());
    }

    [Fact]
    public void Changed_projection_size_restarts_scan()
    {
        var cursor = new ProjectionFrontierCursor();
        cursor.Take(10, 7).ToArray();

        Assert.Equal(new[] { 0, 1 }, cursor.Take(2, 8).ToArray());
        Assert.Equal(new[] { 0, 1 }, cursor.Take(2, 8).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Empty_projection_returns_no_indices(int count)
    {
        var cursor = new ProjectionFrontierCursor();
        Assert.Empty(cursor.Take(count, 32));
    }
}
