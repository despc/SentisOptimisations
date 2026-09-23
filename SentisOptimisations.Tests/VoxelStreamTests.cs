using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using VRage.Library.Collections;
using VRage.Network;
using Xunit;

namespace SentisOptimisations.Tests;

/// <summary>
/// What the voxel streaming fix stands on: the method it replaces, the pieces it calls, and the
/// state the streaming protocol uses while the data is being made on a worker thread.
/// </summary>
public class VoxelStreamTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static Type Replicable =>
        typeof(MyVoxelBase).Assembly.GetType("Sandbox.Game.Replication.MyVoxelReplicable");

    [Fact]
    public void The_replaced_serialize_has_the_signature_the_prefix_declares()
    {
        Assert.NotNull(Replicable);
        var serialize = Replicable.GetMethod("Serialize", Any, null,
            new[] { typeof(BitStream), typeof(HashSet<string>), typeof(Endpoint), typeof(Action) }, null);
        Assert.NotNull(serialize);
        Assert.Equal(typeof(void), serialize.ReturnType);
    }

    [Fact]
    public void The_replicable_exposes_its_voxel_and_the_serializing_scope_exists()
    {
        var instance = Replicable.GetProperty("Instance", Any | BindingFlags.FlattenHierarchy);
        Assert.NotNull(instance);
        Assert.True(typeof(MyVoxelBase).IsAssignableFrom(instance.PropertyType),
            "Instance no longer gives the voxel the prefix serializes");

        var scope = typeof(MyReplicationLayer).GetMethod("StartSerializingReplicable",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(scope);
        Assert.True(typeof(IDisposable).IsAssignableFrom(scope.ReturnType), "the serializing scope is not disposable");
    }

    [Fact]
    public void The_storage_blob_is_built_by_the_method_the_fix_moves_off_the_game_thread()
    {
        var storage = typeof(MyVoxelBase).Assembly.GetType("Sandbox.Engine.Voxels.MyStorageBase");
        Assert.NotNull(storage);

        var save = storage.GetMethod("Save", Any, null, new[] { typeof(byte[]).MakeByRefType() }, null);
        Assert.NotNull(save);
        Assert.True(save.GetParameters().Single().IsOut, "Save no longer hands the blob back through an out parameter");

        // The background cache reads this to know whether a blob is there at all.
        Assert.NotNull(storage.GetProperty("AreDataCached", Any));
        // And every change of the storage throws that blob away, which is why it has to be rebuilt.
        Assert.NotNull(storage.GetMethod("ResetDataCache", Any));
        Assert.NotNull(storage.GetEvent("RangeChanged", Any));
    }

    [Fact]
    public void The_protocol_can_wait_while_the_blob_is_being_made()
    {
        // The state group answers Processing until writeData() is called, and the replication server
        // asks again later; that is what makes it safe to build the blob on a worker thread.
        var state = typeof(MyReplicationLayer).Assembly.GetType("VRage.Network.MyStreamProcessingState");
        Assert.NotNull(state);
        var names = Enum.GetNames(state);
        Assert.Contains("Processing", names);
        Assert.Contains("Finished", names);
    }
}

/// <summary>The join path: the add the budget throttles and the serializers the warm-up builds.</summary>
public class ReplicableAddTests
{
    [Fact]
    public void The_add_the_budget_patches_exists_with_its_force_flag()
    {
        var add = typeof(VRage.Network.MyReplicationServer).GetMethod("AddForClient",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(add);
        var parameters = add.GetParameters();
        Assert.Equal("force", parameters[3].Name);
        Assert.Equal(typeof(bool), parameters[3].ParameterType);
        // The prefix reads the replicable to report a slow one.
        Assert.Equal(typeof(VRage.Network.IMyReplicable), parameters[0].ParameterType);
    }

    [Fact]
    public void The_loop_that_retries_next_frame_still_exists()
    {
        // The budget only works because UpdateBefore walks the client's layers every frame and adds
        // whatever it still lacks; without that, a delayed add would never happen.
        Assert.NotNull(typeof(VRage.Network.MyReplicationServer).GetMethod("UpdateBefore",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
    }

    [Fact]
    public void The_object_builders_the_warmup_serializes_can_be_created()
    {
        // The warm-up builds them with new(); the game's factory needs a loaded object builder
        // registry, which a unit test does not have.
        foreach (var type in new[]
                 {
                     typeof(VRage.Game.MyObjectBuilder_Character),
                     typeof(VRage.Game.MyObjectBuilder_CubeGrid),
                     typeof(VRage.Game.MyObjectBuilder_FloatingObject),
                 })
        {
            Assert.False(type.IsAbstract, type.Name + " cannot be created by the warm-up");
            Assert.NotNull(type.GetConstructor(Type.EmptyTypes));
            Assert.True(typeof(VRage.ObjectBuilders.MyObjectBuilder_Base).IsAssignableFrom(type));
        }
    }
}

/// <summary>The two calls the character update-10 patch rewrites.</summary>
public class CharacterUpdate10Tests
{
    [Fact]
    public void The_update_and_both_calls_it_rewrites_exist()
    {
        var update10 = typeof(Sandbox.Game.Entities.Character.MyCharacter).GetMethod(
            nameof(Sandbox.Game.Entities.Character.MyCharacter.UpdateBeforeSimulation10),
            BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
        Assert.NotNull(update10);

        var distributor = typeof(Sandbox.Game.EntityComponents.MyResourceDistributorComponent).GetMethod(
            "UpdateBeforeSimulation", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
        var broadcasters = typeof(Sandbox.Game.Entities.Cube.MyRadioReceiver).GetMethod(
            "UpdateBroadcastersInRange", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
        Assert.NotNull(distributor);
        Assert.NotNull(broadcasters);

        // Both have to be in the body, or the transpiler refuses to patch.
        var il = update10.GetMethodBody().GetILAsByteArray();
        var module = update10.Module;
        var called = new List<MethodBase>();
        for (var i = 0; i < il.Length - 4; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F) continue;
            try { called.Add(module.ResolveMethod(BitConverter.ToInt32(il, i + 1))); }
            catch (Exception) { }
        }

        // The radio call is emitted against the base declaration, which is why the transpiler
        // compares GetBaseDefinition() and not the override.
        Assert.Contains(called.OfType<MethodInfo>(), m => m.Name == distributor.Name && m.GetParameters().Length == 0);
        Assert.Contains(called.OfType<MethodInfo>(), m => m.Name == broadcasters.Name && m.GetParameters().Length == 0);
    }

    [Fact]
    public void The_period_leaves_the_delay_under_a_second()
    {
        // Vanilla does this every 10 frames; the patch does it every Period * 10.
        Assert.InRange(Optimizer.Optimizations.CharacterUpdate10.Period, 2, 6);
    }
}

/// <summary>The call the antenna update-10 patch rewrites, and what it reads to decide.</summary>
public class AntennaUpdate10Tests
{
    [Fact]
    public void The_antenna_update_holds_exactly_one_radio_call()
    {
        var update10 = typeof(Sandbox.Game.Entities.Cube.MyRadioAntenna).GetMethod(
            nameof(Sandbox.Game.Entities.Cube.MyRadioAntenna.UpdateAfterSimulation10),
            BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
        Assert.NotNull(update10);
        Assert.Equal(typeof(Sandbox.Game.Entities.Cube.MyRadioAntenna), update10.DeclaringType);

        var il = update10.GetMethodBody().GetILAsByteArray();
        var module = update10.Module;
        var called = new List<MethodBase>();
        for (var i = 0; i < il.Length - 4; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F) continue;
            try { called.Add(module.ResolveMethod(BitConverter.ToInt32(il, i + 1))); }
            catch (Exception) { }
        }

        // The transpiler refuses to patch unless it finds exactly one.
        Assert.Single(called.OfType<MethodInfo>(), m => m.Name == "UpdateBroadcastersInRange" && m.GetParameters().Length == 0);
    }

    [Fact]
    public void The_grid_tells_whether_a_player_sees_it()
    {
        var tier = typeof(Sandbox.Game.Entities.MyCubeGrid).GetProperty("PlayerPresenceTier");
        Assert.NotNull(tier);
        Assert.Equal(typeof(VRage.Game.ModAPI.MyUpdateTiersPlayerPresence), tier.PropertyType);
        // Once in 100 frames at most: the relay notices a new antenna under two seconds later.
        Assert.InRange(Optimizer.Optimizations.AntennaUpdate10.IdlePeriod, 2, 10);
        // Half a second at most where a player can see it.
        Assert.InRange(Optimizer.Optimizations.AntennaUpdate10.SeenPeriod, 2, 3);
    }
}

/// <summary>What the idle turret search skip reads from the game.</summary>
public class TurretIdleSearchTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void The_turret_search_and_what_it_looks_at_exist()
    {
        var system = typeof(Sandbox.Game.Weapons.MyLargeTurretTargetingSystem);
        Assert.NotNull(system.GetMethod("CheckAndSelectNearTargetsParallel", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null));
        Assert.Equal(typeof(Sandbox.Game.Entities.Interfaces.IMyTargetingReceiver), system.GetField("m_targetReceiver", Private)?.FieldType);
        Assert.Equal(typeof(Sandbox.Game.Entities.MyCubeGrid), system.GetField("m_focusedTarget", Private)?.FieldType);

        var grid = typeof(Sandbox.Game.EntityComponents.MyGridTargeting);
        Assert.Equal(typeof(int), grid.GetField("m_lastScan", Private)?.FieldType);
        Assert.Equal(typeof(List<VRage.Game.Entity.MyEntity>), grid.GetField("m_targetRoots", Private)?.FieldType);
        Assert.Equal(typeof(Dictionary<Sandbox.Game.Entities.MyCubeGrid, VRage.Game.ModAPI.Ingame.MyGridTargetingRelationFiltering>),
            grid.GetField("m_gridToRelation", Private)?.FieldType);
        Assert.NotNull(grid.GetProperty("ScanLock"));
    }

    [Fact]
    public void The_skip_trusts_a_scan_only_as_long_as_the_game_does()
    {
        // MyGridTargeting.RescanIfNeeded rescans after 100 frames; the skip must not trust it longer.
        var frames = (int)typeof(Optimizer.Optimizations.TurretIdleSearch)
            .GetField("ScanFrames", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
        Assert.Equal(100, frames);
    }
}
