using System;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using SentisOptimisationsPlugin;
using SpaceEngineers.Game.Entities.Blocks;
using Xunit;

namespace SentisOptimisations.Tests;

/// <summary>Idle air vents run once in <see cref="AirVentIdle.IdlePeriod"/> frames, each on its own frame.</summary>
public class AirVentIdleTests
{
    [Fact]
    public void A_vent_runs_once_a_period()
    {
        var random = new Random(7);
        for (var i = 0; i < 1000; i++)
        {
            var id = (long)(random.NextDouble() * long.MaxValue) * (i % 2 == 0 ? 1 : -1);
            var start = random.Next(0, int.MaxValue - 1000);
            Assert.Equal(1, Enumerable.Range(start, AirVentIdle.IdlePeriod).Count(f => AirVentIdle.Due(id, f)));
        }
    }

    [Fact]
    public void A_vent_with_gas_runs_once_in_ten()
    {
        var random = new Random(3);
        for (var i = 0; i < 1000; i++)
        {
            var id = (long)(random.NextDouble() * long.MaxValue);
            var start = random.Next(0, int.MaxValue - 1000);
            Assert.Equal(10, Enumerable.Range(start, AirVentIdle.IdlePeriod).Count(f => AirVentIdle.Due(id, f, AirVentIdle.ActivePeriod)));
            // the idle run is one of the active ones: a vent that starts moving gas is not run twice in a row
            Assert.All(Enumerable.Range(start, AirVentIdle.IdlePeriod).Where(f => AirVentIdle.Due(id, f)), f => Assert.True(AirVentIdle.Due(id, f, AirVentIdle.ActivePeriod)));
        }
    }

    [Fact]
    public void The_vents_are_spread_over_the_frames()
    {
        // entity ids of the game: random 56 bits with the type in the top byte
        var random = new Random(11);
        var perFrame = new int[AirVentIdle.IdlePeriod];
        const int vents = 10000;
        for (var i = 0; i < vents; i++)
        {
            var bytes = new byte[8];
            random.NextBytes(bytes);
            bytes[7] = 0x2C;
            perFrame[AirVentIdle.Phase(BitConverter.ToInt64(bytes, 0))]++;
        }
        var mean = vents / AirVentIdle.IdlePeriod;
        Assert.True(perFrame.Max() < mean * 1.5 && perFrame.Min() > mean * 0.5,
            "frames from " + perFrame.Min() + " to " + perFrame.Max() + " vents, " + mean + " a frame on average");
    }

    [Fact]
    public void The_patched_members_exist()
    {
        var update = typeof(MyAirVent).GetMethod(nameof(MyAirVent.UpdateAfterSimulation), BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
        Assert.NotNull(update);
        Assert.Equal(typeof(MyAirVent), update.DeclaringType);
        var transfer = typeof(MyAirVent).GetMethod("Transfer", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(transfer);
        Assert.Equal(typeof(float), transfer.GetParameters().Single().ParameterType);
        Assert.NotNull(typeof(MyCubeBlock).GetMethod("get_HasDamageEffect", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
    }
}
