using SentisOptimisationsPlugin.CrashFix;
using VRageMath;
using Xunit;

namespace SentisOptimisations.Tests
{
public class CharacterMatrixGuardTests
{
    [Fact]
    public void A_rotation_is_not_scaled()
    {
        var m = MatrixD.CreateWorld(new Vector3D(1, 2, 3), Vector3D.Normalize(new Vector3D(1, 1, 0)), Vector3D.Backward);
        Assert.False(CharacterMatrixGuard.Scaled(ref m));
    }

    [Fact]
    public void The_bot_matrix_of_29_09_is_scaled()
    {
        var m = new MatrixD(-621108800, -2209478144, -2783635200, 0, 2209505280, -240068208, -302349632, 0,
            2783613696, -302549504, -381040608, 0, -1988651120.87, 215947997.0, 271916578.6, 1);
        Assert.True(CharacterMatrixGuard.Scaled(ref m));
    }

    [Fact]
    public void Not_a_number_is_scaled()
    {
        var m = MatrixD.Identity;
        m.M11 = double.NaN;
        Assert.True(CharacterMatrixGuard.Scaled(ref m));
    }

    [Fact]
    public void Places()
    {
        Assert.True(CharacterMatrixGuard.SanePlace(new Vector3D(-96409, -113385, -198085)));
        Assert.False(CharacterMatrixGuard.SanePlace(new Vector3D(-1988651120.87, 0, 0)));
        Assert.False(CharacterMatrixGuard.SanePlace(new Vector3D(double.NaN, 0, 0)));
    }
}
}
