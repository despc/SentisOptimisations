using System;
using System.Reflection;
using System.Runtime.Serialization;
using Sandbox.Game.World.Generator;
using SentisGameplayImprovements;
using Xunit;

namespace SentisOptimisations.Tests;

/// <summary>SentisGameplayImprovements' contract reward multipliers.</summary>
public class ContractPriceTests
{
    private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData(10000, 10.0, 100000)]
    [InlineData(10000, 1.0, 10000)]
    [InlineData(10000, 1.05, 10500)]
    [InlineData(10000, 0.5, 5000)]
    [InlineData(12345, 2.5, 30862)]
    [InlineData(0, 30.0, 0)]
    public void The_reward_is_multiplied(long reward, double multiplier, long expected)
    {
        Assert.Equal(expected, ContractPricePatch.Scale(reward, multiplier));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void A_negative_or_broken_multiplier_gives_nothing(double multiplier)
    {
        Assert.Equal(0, ContractPricePatch.Scale(10000, multiplier));
    }

    [Theory]
    [InlineData(long.MaxValue / 2, 10.0)]
    [InlineData(1000, double.PositiveInfinity)]
    [InlineData(long.MaxValue, 1.5)]
    public void A_reward_too_big_for_a_long_stays_the_biggest_long(long reward, double multiplier)
    {
        Assert.Equal(long.MaxValue, ContractPricePatch.Scale(reward, multiplier));
    }

    [Fact]
    public void Every_target_exists_returns_a_long_and_has_its_suffix()
    {
        Assert.Equal(4, ContractPricePatch.Targets.Length);
        foreach (var (type, method, suffix) in ContractPricePatch.Targets)
        {
            var target = type.GetMethod(method, Any);
            Assert.True(target != null, type.Name + "." + method + " is gone");
            Assert.Equal(typeof(long), target.ReturnType);

            var patch = typeof(ContractPricePatch).GetMethod(suffix, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(patch);
            var parameter = Assert.Single(patch.GetParameters());
            Assert.Equal("__result", parameter.Name);
            Assert.Equal(typeof(long).MakeByRefType(), parameter.ParameterType);
        }
    }

    // The game's own reward method, scaled, gives what the plugin's copy of the formula gave before: the
    // switch to scaling the game's result changed no reward (the old copy kept a fraction the game drops).

    private static long Vanilla(Type type, string method, params object[] args)
    {
        var target = type.GetMethod(method, Any);
        var instance = target.IsStatic ? null : FormatterServices.GetUninitializedObject(type);
        return (long)target.Invoke(instance, args);
    }

    [Theory]
    [InlineData(20000L, 1, 30.0)]
    [InlineData(20000L, 37, 30.0)]
    [InlineData(55000L, 1000, 2.5)]
    public void Acquisition_as_before(long baseRew, int amount, double m)
    {
        var old = (long)(baseRew * Math.Pow(2.0, Math.Log10(amount)) * m);
        var now = ContractPricePatch.Scale(Vanilla(typeof(MyContractTypeAcquisitionStrategy), "GetMoneyRewardForAcquisitionContract", baseRew, amount), m);
        Assert.InRange(now, old - (long)Math.Ceiling(m), old);
    }

    [Theory]
    [InlineData(30000L, 5000.0, 10.0)]
    [InlineData(30000L, 250000.0, 10.0)]
    public void Escort_as_before(long baseRew, double distance, double m)
    {
        var old = (long)(baseRew * Math.Pow(3.0, Math.Log10(distance)) * m);
        var now = ContractPricePatch.Scale(Vanilla(typeof(MyContractTypeEscortStrategy), "GetMoneyReward_Escort", baseRew, distance), m);
        Assert.InRange(now, old - (long)Math.Ceiling(m), old);
    }

    [Theory]
    [InlineData(40000L, 100000.0, 2000, 10.0)]
    [InlineData(40000L, 3000000.0, 5500, 3.0)]
    public void Hauling_as_before(long baseReward, double distance, int uraniumPrice, double m)
    {
        var num1 = distance / 2000000.0;
        var old = (long)((baseReward + baseReward * num1 + num1 * (uraniumPrice * (double)3.75f)) * m);
        var now = ContractPricePatch.Scale(Vanilla(typeof(MyContractTypeBaseStrategy), "GetHaulingMoneyReward", baseReward, distance, uraniumPrice), m);
        Assert.InRange(now, old - (long)Math.Ceiling(m), old);
    }

    [Theory]
    [InlineData(25000L, 8000.0, 1500000L, 0.1f, 10.0)]
    [InlineData(25000L, 120000.0, 90000000L, 0.05f, 4.0)]
    public void Repair_as_before(long baseRew, double gridDistance, long gridPrice, float coef, double m)
    {
        var old = (long)((baseRew * Math.Pow(2.0, Math.Log10(gridDistance)) + (long)(coef * (double)gridPrice)) * m);
        var now = ContractPricePatch.Scale(Vanilla(typeof(MyContractTypeRepairStrategy), "GetMoneyRewardForRepairContract", baseRew, gridDistance, gridPrice, coef), m);
        // the game multiplies the grid's price by the coefficient in float, the old copy in double
        Assert.InRange(now, (long)(old * 0.999), (long)(old * 1.001) + 1);
    }
}
