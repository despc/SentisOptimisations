using System;
using System.Collections.Generic;
using Optimizer.Optimizations;
using VRage.Network;
using Xunit;

namespace SentisOptimisations.Tests;

public class EndpointComparersTests
{
    [Fact]
    public void Endpoint_comparer_matches_vanilla_equality_and_hash()
    {
        var values = new[]
        {
            new Endpoint(76561198000000001UL, 0), new Endpoint(76561198000000001UL, 0), new Endpoint(76561198000000001UL, 1),
            new Endpoint(76561198000000002UL, 0), new Endpoint(0UL, 0), new Endpoint(ulong.MaxValue, 255),
        };
        foreach (var a in values)
        foreach (var b in values)
        {
            Assert.Equal(a.Equals((object)b), EndpointComparers.EndpointComparer.Instance.Equals(a, b));
            Assert.Equal(a.Id.Equals((object)b.Id), EndpointComparers.EndpointIdComparer.Instance.Equals(a.Id, b.Id));
        }
        foreach (var a in values)
        {
            Assert.Equal(a.GetHashCode(), EndpointComparers.EndpointComparer.Instance.GetHashCode(a));
            Assert.Equal(a.Id.GetHashCode(), EndpointComparers.EndpointIdComparer.Instance.GetHashCode(a.Id));
        }
    }

    [Fact]
    public void Install_replaces_default_comparers_and_dictionary_lookups_do_not_allocate()
    {
        EndpointComparers.Install();
        Assert.Same(EndpointComparers.EndpointComparer.Instance, EqualityComparer<Endpoint>.Default);
        Assert.Same(EndpointComparers.EndpointIdComparer.Instance, EqualityComparer<EndpointId>.Default);

        var dictionary = new Dictionary<Endpoint, int>();
        for (var i = 0; i < 64; i++) dictionary[new Endpoint((ulong)i + 1, 0)] = i;
        var key = new Endpoint(33UL, 0);
        dictionary.TryGetValue(key, out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sum = 0;
        for (var i = 0; i < 10000; i++)
            if (dictionary.TryGetValue(key, out var value)) sum += value;
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(320000, sum);
    }
}
