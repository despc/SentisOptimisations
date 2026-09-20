using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Havok;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using SentisOptimisationsPlugin;
using SentisOptimisationsPlugin.ShipTool;
using SpaceEngineers.Game.Entities.Blocks;
using Xunit;

namespace SentisOptimisations.Tests;

/// <summary>
/// The members every reviewed patch binds, so a game update breaks a test here instead of the
/// server, plus the behaviour of the logic that can be exercised without a world.
/// </summary>
public class PatchTargetTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void Gas_transfer_targets_exist_with_the_expected_parameters()
    {
        var tank = typeof(MyGasTank).GetMethod("ExecuteGasTransfer", Instance);
        Assert.NotNull(tank);
        Assert.Equal(typeof(double), tank.GetParameters().Single().ParameterType);

        var vent = typeof(MyAirVent).GetMethod("Transfer", Instance);
        Assert.NotNull(vent);
        Assert.Equal(typeof(float), vent.GetParameters().Single().ParameterType);
    }

    [Fact]
    public void Grid_system_targets_exist()
    {
        Assert.NotNull(typeof(MyUpdateableGridSystem).GetProperty("Grid", Instance));
        Assert.NotNull(typeof(MyGridGasSystem).GetMethod("ScheduleUpdate", Instance));
        Assert.NotNull(typeof(MyGridGasSystem).GetMethod("Schedule", Any));
        Assert.NotNull(typeof(MyGridConveyorSystem).GetMethod("Schedule", Any));
        Assert.NotNull(typeof(MyGridConveyorSystem).GetMethod(nameof(MyGridConveyorSystem.UpdateLines),
            BindingFlags.Instance | BindingFlags.Public));
        Assert.NotNull(typeof(MyGridConveyorSystem).GetMethod(nameof(MyGridConveyorSystem.FlagForRecomputation),
            BindingFlags.Instance | BindingFlags.Public));
        var flag = typeof(MyGridConveyorSystem).GetField("m_needsRecomputation", Instance);
        Assert.NotNull(flag);
        Assert.Equal(typeof(bool), flag.FieldType);
    }

    [Fact]
    public void Safe_zone_targets_exist()
    {
        Assert.NotNull(typeof(MySafeZone).GetMethod("phantom_Leave", Instance));
        Assert.NotNull(typeof(MySafeZone).GetMethod("IsSafe", Instance));
        Assert.NotNull(typeof(MySafeZone).GetMethod(nameof(MySafeZone.UpdateBeforeSimulation),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));

        var remove = typeof(MySafeZone).GetMethod("RemoveEntityPhantom", Instance);
        Assert.NotNull(remove);
        Assert.Equal(typeof(HkRigidBody), remove.GetParameters()[0].ParameterType);

        var isSubGridSafe = typeof(MySafeZone).GetMethod("IsSubGridSafe", Instance);
        Assert.NotNull(isSubGridSafe);
        Assert.True(isSubGridSafe.ReturnType.IsEnum, "IsSubGridSafe no longer returns an enum the patch can map");
        Assert.Equal(typeof(MyCubeGrid), isSubGridSafe.GetParameters().Single().ParameterType);
    }

    [Fact]
    public void The_subgrid_result_names_are_the_ones_the_patch_maps()
    {
        var names = Enum.GetNames(typeof(MySafeZone).GetMethod("IsSubGridSafe", Instance).ReturnType)
            .Select(n => n.Replace("_", "").ToUpperInvariant())
            .ToList();
        Assert.Contains("NOTSAFE", names);
        Assert.Contains("SAFE", names);
    }

    [Fact]
    public void Skipped_render_side_methods_exist()
    {
        Assert.NotNull(typeof(MyEntity3DSoundEmitter).GetMethod(
            nameof(MyEntity3DSoundEmitter.Update), BindingFlags.Instance | BindingFlags.Public));
        Assert.NotNull(typeof(MyThrust).GetMethod("RenderUpdate",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        Assert.NotNull(typeof(Sandbox.Game.Entities.Character.MyCharacter).GetMethod("UpdateHeadAndWeapon", Instance));
    }
}

public class GasTransferBatchTests
{
    private static readonly Type BatchType = typeof(GasTankOptimisations)
        .GetNestedType("Batch", BindingFlags.NonPublic);

    private static object NewBatch() => Activator.CreateInstance(BatchType, true);

    private static bool Add(object batch, double amount, DateTime now, out double total)
    {
        var args = new object[] { amount, now, 0.0 };
        var due = (bool)BatchType.GetMethod("Add").Invoke(batch, args);
        total = (double)args[2];
        return due;
    }

    private static bool Take(object batch, out double total)
    {
        var args = new object[] { 0.0 };
        var owed = (bool)BatchType.GetMethod("Take").Invoke(batch, args);
        total = (double)args[0];
        return owed;
    }

    [Fact]
    public void Gathers_until_the_batch_is_full_and_then_hands_over_the_sum()
    {
        var batch = NewBatch();
        var now = DateTime.UtcNow;
        double total = 0;
        for (var i = 1; i < GasTankOptimisations.TransfersPerFlush; i++)
            Assert.False(Add(batch, 2, now, out total), "handed over after " + i + " transfers");

        Assert.True(Add(batch, 2, now, out total), "never handed over");
        Assert.Equal(2 * GasTankOptimisations.TransfersPerFlush, total, 6);
    }

    [Fact]
    public void Nothing_is_lost_when_a_block_goes_quiet()
    {
        var batch = NewBatch();
        var now = DateTime.UtcNow;
        Add(batch, 1.5, now, out _);
        Add(batch, 2.5, now, out _);
        Assert.True(Take(batch, out var owed));
        Assert.Equal(4.0, owed, 6);
        Assert.False(Take(batch, out _), "an empty batch still claims it owes something");
    }

    [Fact]
    public void A_batch_that_waited_too_long_is_handed_over_early()
    {
        var batch = NewBatch();
        var started = DateTime.UtcNow;
        Assert.False(Add(batch, 1, started, out _));
        Assert.True(Add(batch, 1, started.AddSeconds(30), out var total), "a stale batch was not handed over");
        Assert.Equal(2.0, total, 6);
    }

    [Fact]
    public void The_batch_starts_over_after_it_was_handed_over()
    {
        var batch = NewBatch();
        var now = DateTime.UtcNow;
        for (var i = 0; i < GasTankOptimisations.TransfersPerFlush; i++) Add(batch, 1, now, out _);
        Assert.False(Add(batch, 5, now, out var total), "the batch did not start over");
        Assert.Equal(5.0, total, 6);
    }
}

public class QueueLoopTests
{
    [Fact]
    public void Ship_tool_actions_run_without_waiting_for_a_poll()
    {
        var queue = new ShipToolsAsyncQueues();
        queue.OnLoaded();
        try
        {
            var ran = new ManualResetEventSlim();
            queue.EnqueueAction(() => ran.Set());
            // The old loop slept 160 ms between looks; waiting on the queue makes this immediate.
            Assert.True(ran.Wait(TimeSpan.FromMilliseconds(100)), "the action waited for a poll");
        }
        finally
        {
            queue.OnUnloading();
        }
    }

    [Fact]
    public void Nothing_runs_after_the_world_is_unloaded()
    {
        var queue = new ShipToolsAsyncQueues();
        queue.OnLoaded();
        queue.OnUnloading();
        Thread.Sleep(50);
        var ran = 0;
        queue.EnqueueAction(() => Interlocked.Increment(ref ran));
        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref ran));
    }
}
