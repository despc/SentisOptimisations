using System;
using SentisOptimisationsPlugin;
using VRageMath;
using Xunit;

namespace SentisOptimisations.Tests
{
public class AsteroidNearReplicationTests
{
    private static readonly BoundingBoxD Asteroid = new BoundingBoxD(new Vector3D(-256), new Vector3D(256));

    [Fact]
    public void A_player_inside_the_asteroids_box_is_near()
    {
        Assert.True(AsteroidNearReplication.Near(Asteroid, Vector3D.Zero));
    }

    [Fact]
    public void A_player_500_m_off_its_face_is_near_and_501_is_not()
    {
        Assert.True(AsteroidNearReplication.Near(Asteroid, new Vector3D(256 + 500, 0, 0)));
        Assert.False(AsteroidNearReplication.Near(Asteroid, new Vector3D(256 + 501, 0, 0)));
    }

    [Fact]
    public void The_distance_is_to_the_box_not_to_its_centre()
    {
        // 700 m from the centre, 444 m from the face
        Assert.True(AsteroidNearReplication.Near(Asteroid, new Vector3D(0, 700, 0)));
    }

    [Fact]
    public void A_box_inflated_outwards_covers_the_asteroid()
    {
        var inflated = Asteroid;
        inflated.Inflate(new Vector3D(5000));
        Assert.True(AsteroidNearReplication.Covers(inflated, Asteroid));
    }

    [Fact]
    public void The_box_of_sync_15_km_view_15_km_is_turned_inside_out_and_does_not_cover_it()
    {
        // MyVoxelReplicable: inflated by sqrt(view^2 / 3) - sync - halfExtents
        var by = Math.Sqrt(15000.0 * 15000.0 / 3) - 15000 - 256;
        var game = Asteroid;
        game.Inflate(new Vector3D(by));
        Assert.True(game.Min.X > game.Max.X);
        Assert.False(AsteroidNearReplication.Covers(game, Asteroid));
    }
}
}
