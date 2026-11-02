using System.Collections.Generic;
using SentisOptimisationsPlugin.Freezer;
using Xunit;

namespace SentisOptimisations.Tests;

public class FrameBudgetQueueTests
{
    private static (int key, int frames) Merge((int key, int frames) waiting, (int key, int frames) again) =>
        (again.key, waiting.frames + again.frames);

    [Fact]
    public void Runs_in_order_until_the_budget_is_spent()
    {
        var queue = new FrameBudgetQueue<(int key, int frames)>();
        for (var i = 0; i < 10; i++) queue.Enqueue(i, (i, 100), Merge);
        var ran = new List<int>();
        var clock = 0.0;
        // each item costs 0.6 ms: 1.5 ms of budget runs three
        var done = queue.Drain(1.5, () => clock, item => { ran.Add(item.key); clock += 0.6; });
        Assert.Equal(3, done);
        Assert.Equal(new[] { 0, 1, 2 }, ran);
        Assert.Equal(7, queue.Count);
    }

    [Fact]
    public void At_least_one_a_frame_even_with_no_budget()
    {
        var queue = new FrameBudgetQueue<(int key, int frames)>();
        queue.Enqueue(1, (1, 100), Merge);
        queue.Enqueue(2, (2, 100), Merge);
        Assert.Equal(1, queue.Drain(0, () => 5, _ => { }));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void A_key_queued_again_is_merged_and_keeps_its_place()
    {
        var queue = new FrameBudgetQueue<(int key, int frames)>();
        Assert.True(queue.Enqueue(1, (1, 100), Merge));
        Assert.True(queue.Enqueue(2, (2, 100), Merge));
        Assert.False(queue.Enqueue(1, (1, 50), Merge));
        Assert.Equal(2, queue.Count);
        var ran = new List<(int key, int frames)>();
        queue.Drain(double.MaxValue, () => 0, ran.Add);
        Assert.Equal(new[] { (1, 150), (2, 100) }, ran);
    }
}
