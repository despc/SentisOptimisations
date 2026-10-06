using System.Reflection;
using Optimizer.Optimizations;
using Sandbox.Engine.Voxels;
using Xunit;

namespace SentisOptimisations.Tests;

public class VoxelShapeHoldTests
{
    [Fact]
    public void The_voxel_body_has_the_methods_patched()
    {
        Assert.NotNull(typeof(MyVoxelPhysicsBody).GetMethod("RequestShapeBatchBlockingInternal", BindingFlags.Instance | BindingFlags.NonPublic));
        var discard = typeof(MyVoxelPhysicsBody).GetMethod("CheckAndDiscardShapes", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(discard);
        Assert.Equal(typeof(void), discard.ReturnType);
        Assert.Empty(discard.GetParameters());
    }

    [Fact]
    public void Meshes_are_held_for_ten_minutes_after_Havok_asked()
    {
        Assert.True(VoxelShapeHold.Holds(1000, 1000));
        Assert.True(VoxelShapeHold.Holds(1000, 1000 + VoxelShapeHold.HoldFrames - 1));
        Assert.False(VoxelShapeHold.Holds(1000, 1000 + VoxelShapeHold.HoldFrames));
        Assert.Equal(10, VoxelShapeHold.HoldFrames / 3600);
        // a frame counter that went back (a new session): nothing held
        Assert.False(VoxelShapeHold.Holds(1000, 10));
    }
}
