using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems;
using SentisOptimisationsPlugin;
using VRage.Groups;
using Xunit;

namespace SentisOptimisations.Tests;

public class PBFixTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData("m_isRunning", typeof(bool))]
    [InlineData("m_echoOutput", typeof(System.Text.StringBuilder))]
    [InlineData("m_currentAssembly", typeof(Assembly))]
    [InlineData("m_needsInstantiation", typeof(bool))]
    [InlineData("m_storageData", typeof(string))]
    [InlineData("m_groupCache", typeof(List<MyCubeGrid>))]
    public void The_fields_the_prefix_binds_exist(string name, Type type)
    {
        var field = typeof(MyProgrammableBlock).GetField(name, Instance);
        Assert.NotNull(field);
        Assert.Equal(type, field.FieldType);
    }

    [Fact]
    public void The_fields_whose_type_is_private_or_an_enum_exist()
    {
        Assert.NotNull(typeof(MyProgrammableBlock).GetField("m_terminationReason", Instance));
        Assert.NotNull(typeof(MyProgrammableBlock).GetField("m_instance", Instance));
        Assert.NotNull(typeof(MyProgrammableBlock).GetField("m_compilerErrors", Instance));
        Assert.NotNull(typeof(MyProgrammableBlock).GetField("m_terminalWrapper", Instance));
    }

    [Fact]
    public void The_methods_the_prefix_binds_exist()
    {
        Assert.NotNull(typeof(MyProgrammableBlock).GetMethod("RunSandboxedProgramAction", Any));
        Assert.NotNull(typeof(MyProgrammableBlock).GetMethod("CreateInstance", Instance));
        Assert.NotNull(typeof(MyProgrammableBlock).GetMethod("Compile", Instance));

        var core = typeof(MyProgrammableBlock).GetMethod("RunSandboxedProgramActionCore", Instance);
        Assert.NotNull(core);
        var parameters = core.GetParameters();
        Assert.Equal(typeof(Action<Sandbox.ModAPI.IMyGridProgram>), parameters[0].ParameterType);
        Assert.True(parameters[1].IsOut);
    }

    [Fact]
    public void The_terminal_wrapper_is_where_the_prefix_looks_for_it()
    {
        var wrapper = typeof(MyProgrammableBlock).GetNestedType("MyGridTerminalWrapper",
            BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(wrapper);
        Assert.NotNull(wrapper.GetMethod("SetInstance", Any));
        // It implements the Ingame interface only: the prefix used to cast it to the ModAPI one.
        Assert.True(typeof(Sandbox.ModAPI.Ingame.IMyGridTerminalSystem).IsAssignableFrom(wrapper));
        Assert.False(typeof(Sandbox.ModAPI.IMyGridTerminalSystem).IsAssignableFrom(wrapper));
        Assert.Equal(typeof(Sandbox.ModAPI.Ingame.IMyGridTerminalSystem),
            typeof(Sandbox.ModAPI.IMyGridProgram).GetProperty("GridTerminalSystem").PropertyType);
    }

    [Fact]
    public void The_logical_group_members_the_prefix_uses_exist()
    {
        var terminalSystem = (MemberInfo)typeof(MyGridLogicalGroupData).GetField("TerminalSystem", Any) ??
                             typeof(MyGridLogicalGroupData).GetProperty("TerminalSystem", Any);
        Assert.NotNull(terminalSystem);

        var update = typeof(MyGridLogicalGroupData).GetMethod("UpdateGridOwnership", Any);
        Assert.NotNull(update);
        Assert.Equal(new[] { typeof(List<MyCubeGrid>), typeof(long) },
            Array.ConvertAll(update.GetParameters(), p => p.ParameterType));
    }

    [Fact]
    public void The_ownership_recalculation_is_patchable()
    {
        var manager = typeof(MyProgrammableBlock).Assembly.GetType("Sandbox.Game.Entities.Cube.MyCubeGridOwnershipManager");
        Assert.NotNull(manager);
        Assert.NotNull(manager.GetMethod("RecalculateOwnersInternal", Instance));
        Assert.NotNull(manager.GetField("m_grid", Instance));
    }

    [Fact]
    public void A_frequent_cheap_script_outweighs_a_rare_expensive_one()
    {
        // The point of measuring per frame: 0.8 ms every frame is a bigger load than 3 ms every 100.
        Assert.True(PerFrame(0.8, 1) > PerFrame(3.0, 100));
    }

    private static double PerFrame(double ms, int frames) => ms / frames;

    [Fact]
    public void The_verdict_window_is_a_window_and_not_a_running_total()
    {
        Assert.InRange(PbLoad.WindowRuns, 2, 32);
    }
}
