using System.Collections.Generic;
using System.Reflection;
using Optimizer.Optimizations;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using VRage.Game.ModAPI;
using Xunit;

namespace SentisOptimisations.Tests;

public class GrinderPatchesTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void Patched_methods_exist_with_the_expected_parameters()
    {
        Assert.NotNull(typeof(MyShipGrinder).GetMethod("StartAnimation", Any));
        Assert.NotNull(typeof(MyShipGrinder).GetMethod("StopAnimation", Any));
        var integrity = typeof(MyCubeGrid).GetMethod(nameof(MyCubeGrid.SendIntegrityChanged), Any);
        Assert.Equal(new[] { typeof(MySlimBlock), typeof(MyIntegrityChangeEnum), typeof(long) },
            System.Array.ConvertAll(integrity.GetParameters(), p => p.ParameterType));
        var stockpile = typeof(MyCubeGrid).GetMethod(nameof(MyCubeGrid.SendStockpileChanged), Any);
        Assert.Equal(new[] { typeof(MySlimBlock), typeof(List<MyStockpileItem>) },
            System.Array.ConvertAll(stockpile.GetParameters(), p => p.ParameterType));
    }

    [Fact]
    public void Construction_model_preload_target_exists()
    {
        var preload = typeof(Sandbox.Definitions.MyCubeBlockDefinition).GetMethod("PreloadConstructionModels",
            BindingFlags.Static | BindingFlags.Public);
        Assert.NotNull(preload);
        Assert.Equal(typeof(Sandbox.Definitions.MyCubeBlockDefinition), preload.GetParameters()[0].ParameterType);
        Assert.Equal("block", preload.GetParameters()[0].Name);
    }

    [Fact]
    public void Prefix_parameter_names_match_the_patched_methods()
    {
        // Torch binds prefix parameters by name.
        var integrity = typeof(MyCubeGrid).GetMethod(nameof(MyCubeGrid.SendIntegrityChanged), Any).GetParameters();
        var prefix = typeof(GrinderPatches).GetMethod("IntegrityPrefix", BindingFlags.Static | BindingFlags.NonPublic).GetParameters();
        for (var i = 0; i < integrity.Length; i++) Assert.Equal(integrity[i].Name, prefix[i + 1].Name);
        var stockpile = typeof(MyCubeGrid).GetMethod(nameof(MyCubeGrid.SendStockpileChanged), Any).GetParameters();
        var stockPrefix = typeof(GrinderPatches).GetMethod("StockpilePrefix", BindingFlags.Static | BindingFlags.NonPublic).GetParameters();
        for (var i = 0; i < stockpile.Length; i++) Assert.Equal(stockpile[i].Name, stockPrefix[i + 1].Name);
    }
}
