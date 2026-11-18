using System.Linq;
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

    // ------------------------------------------------------------------ a script's start

    /// <summary>
    /// Isy's Inventory Manager's first runs after it was switched on, ms, its code compiled to machine code before its first
    /// run (PbCompile; stand, scenario pb_iim_watch, 04.10.2026): three over 2 ms, then none in three minutes.
    /// </summary>
    private static readonly double[] IimStart = { 3.311, 0.057, 0.028, 0.679, 1.192, 6.335, 0.301, 0.072, 0.231, 0.025, 0.023, 8.830, 0.025, 0.193, 0.015 };

    private static PbVerdict Run(PbLoad.Stats stats, ref ulong frame, double ms, object program, ulong every = 10)
    {
        frame += every;
        return PbLoad.Record(stats, frame, ms, false, 2, 0.5, 3, program);
    }

    [Fact]
    public void A_script_compiled_ahead_is_not_punished_for_its_start()
    {
        var stats = new PbLoad.Stats();
        var program = new object();
        ulong frame = 0;
        var verdicts = IimStart.Select(ms => Run(stats, ref frame, ms, program)).ToList();
        while (frame < 3 * 60 * 60) verdicts.Add(Run(stats, ref frame, 0.06, program));
        Assert.DoesNotContain(PbVerdict.Punish, verdicts);
    }

    [Fact]
    public void A_script_heavy_from_its_start_is_punished_at_once()
    {
        var stats = new PbLoad.Stats();
        var program = new object();
        ulong frame = 0;
        var punishedAt = 0;
        for (var i = 1; i <= 100 && punishedAt == 0; i++)
            if (Run(stats, ref frame, 5.0, program, 1) == PbVerdict.Punish) punishedAt = i;
        // its first ten runs not measured, then the fourth over the limit
        Assert.Equal(PbLoad.UnmeasuredRuns + 4, punishedAt);
    }

    [Fact]
    public void A_script_light_per_run_but_on_every_frame_is_punished_by_its_load()
    {
        var stats = new PbLoad.Stats();
        var program = new object();
        ulong frame = 0;
        var punishedAt = 0;
        for (var i = 1; i <= 1000 && punishedAt == 0; i++)
            if (Run(stats, ref frame, 1.5, program, 1) == PbVerdict.Punish) punishedAt = i;
        // its load is judged once its grace after the start is over, and it is over the budget then: at once
        Assert.InRange(punishedAt, (int)PbLoad.LoadGraceFrames, (int)PbLoad.LoadGraceFrames + 10);
    }

    [Fact]
    public void The_first_ten_runs_are_not_measured()
    {
        var stats = new PbLoad.Stats();
        var program = new object();
        ulong frame = 0;
        var verdicts = new List<PbVerdict>();
        for (var i = 0; i < PbLoad.UnmeasuredRuns; i++) verdicts.Add(Run(stats, ref frame, 30.0, program, 1));
        for (var i = 0; i < 30; i++) verdicts.Add(Run(stats, ref frame, 0.05, program));
        Assert.All(verdicts, v => Assert.Equal(PbVerdict.Ok, v));
        Assert.True(stats.LoadMsPerFrame < 0.05, stats.LoadMsPerFrame.ToString());
    }

    [Fact]
    public void A_recompiled_script_has_its_ten_unmeasured_runs_again()
    {
        var stats = new PbLoad.Stats();
        ulong frame = 0;
        var first = new object();
        for (var i = 0; i < 50; i++) Run(stats, ref frame, 0.06, first);
        var second = new object();
        var verdicts = new List<PbVerdict>();
        for (var i = 0; i < PbLoad.UnmeasuredRuns; i++) verdicts.Add(Run(stats, ref frame, 30.0, second));
        Assert.All(verdicts, v => Assert.Equal(PbVerdict.Ok, v));
    }

    [Fact]
    public void A_rare_script_is_not_punished_for_spikes_that_are_already_over()
    {
        // Production report: a grid that wakes now and then did one heavy run per wake,
        // then ran free - and the block was destroyed with "run 0.00 ms, 2.99 ms of every frame".
        // The average has to relax over the frames that passed, not over the runs: a cheap run
        // long after a spike must not inherit the spike's cost.
        var stats = new PbLoad.Stats();
        var program = new object();
        ulong frame = 0;
        for (var i = 0; i < 22; i++) Run(stats, ref frame, 0.05, program, 60);
        for (var i = 0; i < 3; i++) Run(stats, ref frame, 300.0, program, 60);
        var verdicts = new List<PbVerdict>();
        for (var i = 0; i < 20; i++) verdicts.Add(Run(stats, ref frame, 0.01, program, 60));
        Assert.DoesNotContain(PbVerdict.Punish, verdicts);
        Assert.True(stats.LoadMsPerFrame < 0.05, stats.LoadMsPerFrame.ToString());
    }

    [Fact]
    public void A_rare_script_that_is_heavy_every_time_is_still_punished()
    {
        // The other side of the relaxation: 1.5 ms every other frame is 0.75 ms of every frame,
        // over the 0.5 budget while each single run is under the 2 ms per-run limit.
        var stats = new PbLoad.Stats();
        var program = new object();
        ulong frame = 0;
        var punishedAt = 0;
        for (var i = 1; i <= 1000 && punishedAt == 0; i++)
            if (Run(stats, ref frame, 1.5, program, 2) == PbVerdict.Punish) punishedAt = i;
        Assert.InRange(punishedAt, (int)PbLoad.LoadGraceFrames / 2, (int)PbLoad.LoadGraceFrames / 2 + 10);
    }

    [Fact]
    public void A_steady_script_over_its_budget_is_over_it_within_a_second()
    {
        // The longer average still catches a script over the budget on every frame: within a second of it.
        var stats = new PbLoad.Stats();
        var program = new object();
        ulong frame = 0;
        for (var i = 0; i < (int)PbLoad.LoadGraceFrames; i++) Run(stats, ref frame, 0.05, program, 1);
        var overAt = 0;
        for (var i = 1; i <= 120 && overAt == 0; i++)
        {
            Run(stats, ref frame, 1.5, program, 1);
            if (stats.LoadMsPerFrame > 0.5) overAt = i;
        }
        Assert.InRange(overAt, 1, 60);
    }

    // ------------------------------------------------------------------ RTAS (vanilla production, 18.11.2026)

    private static PbVerdict Rtas(PbLoad.Stats stats, ref ulong frame, double ms, object program)
    {
        frame += 1;
        // the production's limits: 0.3 ms a frame, 8 ms a run, 3 overruns of the 20 tolerated
        return PbLoad.Record(stats, frame, ms, false, 8, 0.3, 3, program);
    }

    /// <summary>A run of RTAS on Update1: its main pass every 4th tick, its search every 8th, its blocks anew every 300th.</summary>
    private static double RtasRun(ulong frame) =>
        frame % 300 == 0 ? 4.0 : frame % 300 == 1 ? 2.0 : frame % 8 == 0 ? 0.6 : frame % 4 == 0 ? 0.3 : 0.06;

    [Fact]
    public void A_script_heavy_now_and_then_but_light_in_the_second_is_not_punished()
    {
        // 0.22 ms a frame over a second, its heavy frames a few in each second: the old third of a second
        // average put it over 0.3 for a handful of runs at each of them, and a handful was enough
        var stats = new PbLoad.Stats();
        var program = new object();
        ulong frame = 0;
        var verdicts = new List<PbVerdict>();
        for (var i = 0; i < 60 * 60; i++) verdicts.Add(Rtas(stats, ref frame, RtasRun(frame + 1), program));
        Assert.DoesNotContain(PbVerdict.Punish, verdicts);
    }

    [Fact]
    public void A_script_setting_itself_up_for_seconds_after_a_recompile_is_not_punished()
    {
        // RTAS after a recompile: 1.2 ms of every frame for five seconds while it gathers its blocks and starts its scans
        var stats = new PbLoad.Stats();
        ulong frame = 0;
        var before = new object();
        for (var i = 0; i < 4000; i++) Rtas(stats, ref frame, RtasRun(frame + 1), before);
        var program = new object();
        var verdicts = new List<PbVerdict>();
        for (var i = 0; i < 300; i++) verdicts.Add(Rtas(stats, ref frame, 1.2, program));
        for (var i = 0; i < 60 * 60; i++) verdicts.Add(Rtas(stats, ref frame, RtasRun(frame + 1), program));
        Assert.DoesNotContain(PbVerdict.Punish, verdicts);
    }

    [Fact]
    public void A_script_recompiled_again_soon_is_charged_its_setup_over_the_second()
    {
        // RTAS recompiled twice in half a minute (stand, 18.11.2026): the second instance has no grace, and its setup -
        // 0.6 ms a frame for a fifth of a second, its usual work after it: about 0.27 ms of that second, as measured on the
        // stand - not 0.6 ms "of every frame"
        var stats = new PbLoad.Stats();
        ulong frame = 0;
        var first = new object();
        for (var i = 0; i < 4000; i++) Rtas(stats, ref frame, RtasRun(frame + 1), first);
        var second = new object();
        for (var i = 0; i < 1500; i++) Rtas(stats, ref frame, RtasRun(frame + 1), second);
        var third = new object();
        var verdicts = new List<PbVerdict>();
        for (var i = 0; i < PbLoad.UnmeasuredRuns + 12; i++) verdicts.Add(Rtas(stats, ref frame, 0.6, third));
        for (var i = 0; i < 60 * 30; i++) verdicts.Add(Rtas(stats, ref frame, RtasRun(frame + 1), third));
        Assert.DoesNotContain(PbVerdict.Punish, verdicts);
    }

    [Fact]
    public void A_script_recompiled_again_and_again_gets_the_grace_once_a_minute()
    {
        // 1.2 ms of every frame, recompiled every 5 seconds to stay in the grace: the second instance has none
        var stats = new PbLoad.Stats();
        ulong frame = 0;
        var punished = false;
        for (var compile = 0; compile < 6 && !punished; compile++)
        {
            var program = new object();
            for (var i = 0; i < 300 && !punished; i++) punished = Rtas(stats, ref frame, 1.2, program) == PbVerdict.Punish;
        }
        Assert.True(punished);
        Assert.InRange(frame, 300UL, 700UL);
    }
}
