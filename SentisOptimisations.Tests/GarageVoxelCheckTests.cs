using SentisOptimisationsPlugin.CrashFix;
using Xunit;

namespace SentisOptimisations.Tests
{
public class GarageVoxelCheckTests
{
    [Fact]
    public void A_live_voxel_with_storage_is_read()
    {
        Assert.False(GarageVoxelCheck.Dead(closed: false, markedForClose: false, hasStorage: true));
    }

    [Fact]
    public void A_deleted_physics_sector_is_not_read()
    {
        // MyVoxelPhysics.BeforeDelete drops the storage (the prod crash of 23.11.2026)
        Assert.True(GarageVoxelCheck.Dead(closed: true, markedForClose: true, hasStorage: false));
        Assert.True(GarageVoxelCheck.Dead(closed: false, markedForClose: false, hasStorage: false));
        Assert.True(GarageVoxelCheck.Dead(closed: false, markedForClose: true, hasStorage: true));
    }
}
}
