using System;
using System.Collections.Generic;
using System.Reflection;
using Optimizer.Optimizations;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.EntityComponents;
using Xunit;

namespace SentisOptimisations.Tests;

public class GasGeneratorUpdateTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static MethodInfo Update => typeof(MyGasGenerator).GetMethod(nameof(MyGasGenerator.UpdateAfterSimulation),
        BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);

    [Fact]
    public void The_patched_members_exist()
    {
        Assert.NotNull(Update);
        Assert.NotNull(typeof(MyGasGenerator).GetMethod("SetRemainingCapacities", Any));
        Assert.NotNull(typeof(MyResourceSinkComponent).GetMethod(nameof(MyResourceSinkComponent.Update)));
        var ice = typeof(MyGasGenerator).GetField("m_iceAmount", Any);
        Assert.NotNull(ice);
        Assert.Equal(typeof(float), ice.FieldType);
    }

    [Fact]
    public void The_every_frame_update_calls_both_throttled_methods()
    {
        // The transpiler looks for these two calls; if a game update moves them it must be revisited.
        var called = CalledMethods(Update);
        Assert.Contains(typeof(MyGasGenerator).GetMethod("SetRemainingCapacities", Any), called);
        Assert.Contains((MethodBase)typeof(MyResourceSinkComponent).GetMethod(nameof(MyResourceSinkComponent.Update)), called);
    }

    [Fact]
    public void Generators_are_spread_over_the_period()
    {
        Assert.True(GasGeneratorUpdate.PeriodFrames > 1);
    }

    /// <summary>Every method the body calls, read out of the raw IL (call and callvirt).</summary>
    private static List<MethodBase> CalledMethods(MethodInfo method)
    {
        var il = method.GetMethodBody().GetILAsByteArray();
        var module = method.Module;
        var generics = method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
        var result = new List<MethodBase>();
        for (var i = 0; i < il.Length - 4; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F) continue; // call, callvirt
            try { result.Add(module.ResolveMethod(BitConverter.ToInt32(il, i + 1), generics, null)); }
            catch (Exception) { /* not a token: the byte was part of another instruction */ }
        }
        return result;
    }
}
