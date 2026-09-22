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
    public void Skipped_render_side_methods_exist()
    {
        Assert.NotNull(typeof(MyEntity3DSoundEmitter).GetMethod(
            nameof(MyEntity3DSoundEmitter.Update), BindingFlags.Instance | BindingFlags.Public));
        Assert.NotNull(typeof(MyThrust).GetMethod("RenderUpdate",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        Assert.NotNull(typeof(Sandbox.Game.Entities.Character.MyCharacter).GetMethod("UpdateHeadAndWeapon", Instance));
    }

    [Fact]
    public void Safe_zone_grid_tracking_targets_exist()
    {
        // the zone's own insert and remove, bound as delegates
        Assert.NotNull(Accessors.Field<MySafeZone, VRage.Collections.MyConcurrentHashSet<VRage.Game.ModAPI.IMyCubeGrid>>("m_grids"));
        Assert.NotNull(Accessors.Field<MySafeZone, VRage.Collections.MyConcurrentHashSet<long>>("m_containedEntities"));
        Assert.NotNull(Accessors.Method<MySafeZone, Func<MySafeZone, VRage.Game.Entity.MyEntity, bool>>("InsertEntityInternal"));
        Assert.NotNull(Accessors.Method<MySafeZone, Action<MySafeZone, long>>("SendInsertedEntity"));
        Assert.NotNull(Accessors.Method<MySafeZone, Action<MySafeZone, VRage.Game.Entity.MyEntity>>("RemovedByPhysics"));

        var filters = typeof(Sandbox.Engine.Physics.MyPhysics).GetMethod("InitCollisionFilters", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(filters);
        Assert.Equal(typeof(HkWorld), filters.GetParameters().Single().ParameterType);

        var create = typeof(Sandbox.Engine.Physics.MyPhysicsBody).GetMethod(nameof(Sandbox.Engine.Physics.MyPhysicsBody.CreateFromCollisionObject),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.NotNull(create);
        var filter = create.GetParameters().Single(p => p.Name == "collisionFilter");
        Assert.Equal(typeof(int), filter.ParameterType);
        Assert.Equal(15, filter.DefaultValue);
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

/// <summary>
/// The player-distance patch: its targets, and the shape of its prefixes. The previous version took
/// __result by value in one of them, so that half of the patch silently did nothing.
/// </summary>
public class ReplicablesPatchTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static Type ClientType =>
        typeof(VRage.Network.MyReplicationServer).Assembly.GetType("VRage.Network.MyClient");

    [Fact]
    public void The_patched_methods_and_the_client_state_exist()
    {
        Assert.NotNull(ClientType);
        var state = ClientType.GetField("State", Any);
        Assert.NotNull(state);
        Assert.True(typeof(VRage.Network.MyClientStateBase).IsAssignableFrom(state.FieldType));

        var layers = ClientType.GetMethods(Any | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "CalculateLayerOfReplicable").ToList();
        Assert.NotEmpty(layers);
        foreach (var method in layers)
            Assert.Equal(typeof(VRage.Network.IMyReplicable), method.GetParameters()[0].ParameterType);

        var add = typeof(VRage.Network.MyReplicationServer).GetMethod("AddReplicableToLayer",
            Any | BindingFlags.DeclaredOnly);
        Assert.NotNull(add);
        Assert.Equal(typeof(bool), add.ReturnType);
        Assert.Equal(new[] { "rep", "layer", "client" },
            add.GetParameters().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Both_prefixes_take_the_result_by_reference()
    {
        foreach (var name in new[] { "CalculateLayerOfReplicablePatched", "AddReplicableToLayerPatched" })
        {
            var prefix = typeof(SentisOptimisationsPlugin.ReplicablesPatch)
                .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.True(prefix != null, name + " is gone");
            var result = prefix.GetParameters().Single(p => p.Name == "__result");
            Assert.True(result.ParameterType.IsByRef,
                name + " takes __result by value, so whatever it assigns never leaves the method");
        }
    }

    [Fact]
    public void The_distance_comes_from_the_world_and_not_from_the_plugin_config()
    {
        Assert.NotNull(typeof(VRage.Game.MyObjectBuilder_SessionSettings).GetField("SyncDistance"));
        Assert.True(typeof(SentisOptimisationsPlugin.MainConfig).GetProperty("PlayersSyncDistance") == null,
            "the plugin still has its own sync distance setting");
    }
}

/// <summary>
/// The two places a player is present at: what the anchors are built from, and what the replication
/// suffix reads to give the player's own surroundings the same layers as the ship they steer.
/// </summary>
public class PlayerAnchorTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void A_player_position_is_only_what_it_controls()
    {
        // This is why the anchors exist: the game's own answer leaves the body out, so a player
        // flying a ship from a remote control block had their own surroundings frozen.
        var getPosition = typeof(Sandbox.Game.World.MyPlayer).GetMethod("GetPosition", Any, null, Type.EmptyTypes, null);
        Assert.NotNull(getPosition);
        Assert.NotNull(typeof(Sandbox.Game.World.MyPlayer).GetProperty("Character", Any));
        Assert.NotNull(typeof(Sandbox.Game.World.MyPlayer).GetProperty("Controller", Any));
    }

    [Fact]
    public void Anchors_answer_without_a_session_instead_of_throwing()
    {
        // The freezer asks this from a background loop, including while a world is unloading.
        Assert.False(SentisOptimisationsPlugin.PlayerAnchors.AnyInRadius(new VRageMath.Vector3D(0, 0, 0), 100));
    }

    [Fact]
    public void The_client_layers_the_suffix_walks_are_where_it_looks()
    {
        var clientType = typeof(VRage.Network.MyReplicationServer).Assembly.GetType("VRage.Network.MyClient");
        var layers = clientType.GetField("UpdateLayers", Any);
        Assert.NotNull(layers);
        Assert.True(layers.FieldType.IsArray);

        var layerType = clientType.GetNestedType("UpdateLayer", BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(layerType);
        var descriptor = layerType.GetField("Descriptor", Any);
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(VRage.Network.MyLayers.UpdateLayerDesc), descriptor.FieldType);
        Assert.NotNull(descriptor.FieldType.GetField("Radius"));

        // The overload with a second position is the one the suffix is attached to.
        var withSecond = clientType.GetMethods(Any | BindingFlags.DeclaredOnly)
            .Single(m => m.Name == "CalculateLayerOfReplicable" && m.GetParameters().Length == 2);
        Assert.Equal(typeof(VRageMath.Vector3D?), withSecond.GetParameters()[1].ParameterType);
        Assert.Equal(layerType, withSecond.ReturnType);
    }
}

/// <summary>
/// Torch binds prefix and suffix arguments by parameter name, and a name that does not exist on the
/// target does not fail that one patch - it throws while the patch manager commits, which takes down
/// every patch in the same commit, including other plugins'. So every name is checked here.
/// </summary>
public class PrefixParameterNameTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic;

    private static void AssertNamesMatch(MethodBase target, MethodInfo patch)
    {
        Assert.True(target != null, "the patched method is gone");
        Assert.True(patch != null, "the patch method is gone");
        var known = new HashSet<string>(target.GetParameters().Select(p => p.Name)) { "__instance", "__result" };
        foreach (var parameter in patch.GetParameters())
        {
            Assert.True(known.Contains(parameter.Name),
                patch.Name + " asks for '" + parameter.Name + "', which " + target.DeclaringType?.Name + "." +
                target.Name + " does not have: this fails the whole patch commit");
        }
    }

    [Fact]
    public void The_laser_antenna_wake_prefix_uses_the_games_parameter_name()
    {
        AssertNamesMatch(
            typeof(Sandbox.Game.Entities.Cube.MyLaserAntenna).GetMethod("ConnectTo", Any, null, new[] { typeof(long) }, null),
            typeof(SentisOptimisationsPlugin.Freezer.LaserAntennaWake).GetMethod("ConnectToPrefix", Statics));
    }

    [Fact]
    public void The_replication_prefixes_use_the_games_parameter_names()
    {
        var clientType = typeof(VRage.Network.MyReplicationServer).Assembly.GetType("VRage.Network.MyClient");
        foreach (var layer in clientType.GetMethods(Any | BindingFlags.DeclaredOnly)
                     .Where(m => m.Name == "CalculateLayerOfReplicable"))
        {
            AssertNamesMatch(layer, typeof(SentisOptimisationsPlugin.ReplicablesPatch)
                .GetMethod("CalculateLayerOfReplicablePatched", Statics));
        }

        AssertNamesMatch(
            typeof(VRage.Network.MyReplicationServer).GetMethod("AddReplicableToLayer", Any | BindingFlags.DeclaredOnly),
            typeof(SentisOptimisationsPlugin.ReplicablesPatch).GetMethod("AddReplicableToLayerPatched", Statics));
        AssertNamesMatch(
            typeof(VRage.Network.MyReplicationServer).GetMethod("AddForClient", BindingFlags.Instance | BindingFlags.NonPublic),
            typeof(SentisOptimisationsPlugin.ReplicableAddBudget).GetMethod("AddForClientPrefix", Statics));
    }

    [Fact]
    public void The_gas_transfer_prefixes_use_the_games_parameter_names()
    {
        AssertNamesMatch(
            typeof(Sandbox.Game.Entities.Blocks.MyGasTank).GetMethod("ExecuteGasTransfer", BindingFlags.Instance | BindingFlags.NonPublic),
            typeof(SentisOptimisationsPlugin.GasTankOptimisations).GetMethod("TankTransferPrefix", Statics));
        AssertNamesMatch(
            typeof(SpaceEngineers.Game.Entities.Blocks.MyAirVent).GetMethod("Transfer", BindingFlags.Instance | BindingFlags.NonPublic),
            typeof(SentisOptimisationsPlugin.GasTankOptimisations).GetMethod("VentTransferPrefix", Statics));
    }
}
