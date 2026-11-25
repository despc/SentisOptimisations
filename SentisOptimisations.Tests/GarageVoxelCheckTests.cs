using SentisOptimisationsPlugin.CrashFix;
using Xunit;

namespace SentisOptimisations.Tests
{
public class GarageVoxelCheckTests
{
    [Fact]
    public void A_voxel_with_storage_is_read()
    {
        // (marked for close or not: it keeps its storage, and IsAnyOfPointInside is everyone's - read as the game reads it)
        Assert.False(GarageVoxelCheck.Unreadable(hasStorage: true));
    }

    [Fact]
    public void A_deleted_physics_sector_is_not_read()
    {
        // MyVoxelPhysics.BeforeDelete drops the storage (the prod crash of 23.11.2026)
        Assert.True(GarageVoxelCheck.Unreadable(hasStorage: false));
    }
}
}
