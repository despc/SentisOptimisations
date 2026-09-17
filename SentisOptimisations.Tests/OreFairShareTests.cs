using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests;

public class OreFairShareTests
{
    [Fact]
    public void Empty_refinery_gets_at_most_the_vanilla_amount()
    {
        Assert.Equal(2000, OreFairShare.Allowed(2000, sourceOre: 709_000, refineryInputOre: 0, refineries: 101, heldBySelf: 0));
    }

    [Fact]
    public void Refinery_stops_at_its_share_of_all_network_ore()
    {
        // 709 t over 101 refineries: ~7020 kg each.
        var share = 709_000.0 / 101;
        Assert.Equal(share - 6000, OreFairShare.Allowed(2000, 600_000, 109_000, 101, heldBySelf: 6000), 3);
        Assert.Equal(0, OreFairShare.Allowed(2000, 600_000, 109_000, 101, heldBySelf: 7100));
    }

    [Fact]
    public void Nearly_empty_refinery_always_gets_the_minimum()
    {
        Assert.Equal(OreFairShare.MinPullKg, OreFairShare.Allowed(2000, sourceOre: 500, refineryInputOre: 0, refineries: 101, heldBySelf: 0));
    }

    [Fact]
    public void Single_refinery_is_not_limited()
    {
        Assert.Equal(2000, OreFairShare.Allowed(2000, 10_000, 50_000, 1, 50_000));
    }

    [Fact]
    public void Every_refinery_pulling_in_turn_ends_up_level()
    {
        const int n = 101;
        var held = new double[n];
        double source = 709_000;
        for (var round = 0; round < 20; round++)
        for (var i = 0; i < n; i++)
        {
            var inputs = 0.0;
            foreach (var h in held) inputs += h;
            var pull = System.Math.Min(source, OreFairShare.Allowed(2000, source, inputs, n, held[i]));
            held[i] += pull;
            source -= pull;
        }
        Assert.True(source < 1);
        Assert.InRange(System.Linq.Enumerable.Max(held) - System.Linq.Enumerable.Min(held), 0, 1);
    }
}
