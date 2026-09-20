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
