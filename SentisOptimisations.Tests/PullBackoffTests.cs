using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests;

public class PullBackoffTests
{
    [Fact]
    public void New_state_never_skips()
    {
        var backoff = new PullBackoff();
        Assert.False(backoff.ShouldSkip(frame: 0, maxBackoffFrames: 600, 0));
    }

    private static PullBackoff AfterGrace(long frame = 0)
    {
        var backoff = new PullBackoff();
        for (var i = 0; i < PullBackoff.EmptyPullsBeforeBackoff - 1; i++)
        {
            backoff.OnResult(frame, false, 60, 480, 0);
            Assert.False(backoff.ShouldSkip(frame + 1, 480, 0));
        }
        return backoff;
    }

    [Fact]
    public void Empty_pulls_double_the_wait_up_to_the_cap()
    {
        var backoff = AfterGrace();
        const int max = 480;

        backoff.OnResult(frame: 0, pulledAnything: false, stepFrames: 60, maxBackoffFrames: max, 0);
        Assert.True(backoff.ShouldSkip(119, max, 0));
        Assert.False(backoff.ShouldSkip(120, max, 0));

        backoff.OnResult(120, false, 60, max, 0);
        Assert.True(backoff.ShouldSkip(359, max, 0));
        Assert.False(backoff.ShouldSkip(360, max, 0));

        backoff.OnResult(360, false, 60, max, 0);
        backoff.OnResult(840, false, 60, max, 0);
        backoff.OnResult(1320, false, 60, max, 0);
        Assert.True(backoff.ShouldSkip(1320 + max - 1, max, 0));
        Assert.False(backoff.ShouldSkip(1320 + max, max, 0));
    }

    [Fact]
    public void Successful_pull_resets_the_backoff_and_the_grace_period()
    {
        var backoff = AfterGrace();
        backoff.OnResult(0, false, 60, 600, 0);
        Assert.True(backoff.ShouldSkip(1, 600, 0));
        backoff.OnResult(120, true, 60, 600, 0);

        Assert.False(backoff.ShouldSkip(121, 600, 0));
        backoff.OnResult(180, false, 60, 600, 0);
        Assert.False(backoff.ShouldSkip(181, 600, 0));
    }

    [Fact]
    public void Disabled_cap_never_skips()
    {
        var backoff = AfterGrace();
        backoff.OnResult(0, false, 60, 600, 0);
        Assert.False(backoff.ShouldSkip(1, maxBackoffFrames: 0, 0));
    }

    [Fact]
    public void Changed_generation_cancels_the_wait()
    {
        var backoff = AfterGrace();
        backoff.OnResult(0, false, 60, 480, 0);
        Assert.True(backoff.ShouldSkip(10, 480, 0));

        Assert.False(backoff.ShouldSkip(11, 480, generation: 7));
        backoff.OnResult(11, false, 60, 480, 7);
        Assert.False(backoff.ShouldSkip(12, 480, 7));
    }

    [Fact]
    public void Jitter_shortens_the_wait_to_at_most_half()
    {
        var early = AfterGrace();
        early.OnResult(0, false, 60, 480, 0, jitter: 0.0);
        Assert.True(early.ShouldSkip(59, 480, 0));
        Assert.False(early.ShouldSkip(60, 480, 0));

        var late = AfterGrace();
        late.OnResult(0, false, 60, 480, 0, jitter: 1.0);
        Assert.True(late.ShouldSkip(119, 480, 0));
    }
}
