using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using SentisOptimisationsPlugin;
using VRage.Game;
using Xunit;

namespace SentisOptimisations.Tests;

public class GridStreamBuildersTests
{
    private static MyObjectBuilder_CubeGrid GridWithScripts()
    {
        var grid = new MyObjectBuilder_CubeGrid { DisplayName = "grid" };
        grid.CubeBlocks = new List<MyObjectBuilder_CubeBlock>
        {
            new MyObjectBuilder_CubeBlock(),
            new MyObjectBuilder_MyProgrammableBlock { Program = "private", ShareMode = MyOwnershipShareModeEnum.None },
            new MyObjectBuilder_MyProgrammableBlock { Program = "faction", ShareMode = MyOwnershipShareModeEnum.Faction },
            new MyObjectBuilder_MyProgrammableBlock { Program = "public", ShareMode = MyOwnershipShareModeEnum.All },
        };
        return grid;
    }

    private static string[] Scripts(MyObjectBuilder_CubeGrid grid) =>
        grid.CubeBlocks.OfType<MyObjectBuilder_MyProgrammableBlock>().Select(b => b.Program).ToArray();

    [Fact]
    public void Faction_member_keeps_shared_scripts_only()
    {
        var full = GridWithScripts();
        var masked = GridStreamBuilders.Mask(full, keepFactionShared: true);
        Assert.Equal(new[] { GridStreamBuilders.HiddenScript, "faction", "public" }, Scripts(masked));
        Assert.Equal(new[] { "private", "faction", "public" }, Scripts(full));
    }

    [Fact]
    public void Stranger_keeps_only_scripts_shared_with_everybody()
    {
        var full = GridWithScripts();
        var masked = GridStreamBuilders.Mask(full, keepFactionShared: false);
        Assert.Equal(new[] { GridStreamBuilders.HiddenScript, GridStreamBuilders.HiddenScript, "public" }, Scripts(masked));
    }

    [Fact]
    public void Masked_builder_shares_everything_but_the_hidden_blocks()
    {
        var full = GridWithScripts();
        var masked = GridStreamBuilders.Mask(full, keepFactionShared: true);
        Assert.Equal(full.DisplayName, masked.DisplayName);
        Assert.NotSame(full.CubeBlocks, masked.CubeBlocks);
        Assert.Same(full.CubeBlocks[0], masked.CubeBlocks[0]);
        Assert.NotSame(full.CubeBlocks[1], masked.CubeBlocks[1]);
        Assert.Same(full.CubeBlocks[2], masked.CubeBlocks[2]);
        Assert.Same(full.CubeBlocks[3], masked.CubeBlocks[3]);
    }
}
