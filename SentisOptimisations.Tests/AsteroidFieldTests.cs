using System;
using System.Collections.Generic;
using System.Linq;
using SentisGameplayImprovements;
using VRageMath;
using Xunit;

namespace SentisOptimisations.Tests;

/// <summary>SentisGameplayImprovements' asteroid fields (!sgi spawnfield, spawnfield2): the parts that need no world.</summary>
public class AsteroidFieldTests
{
    [Theory]
    [InlineData(1000, 10, 100, 150, null)]
    [InlineData(1000, 1, 150, 150, null)]
    [InlineData(1000, 0, 100, 150, "number")]
    [InlineData(0, 10, 100, 150, "field size")]
    [InlineData(1000, 10, 0, 150, "asteroid size")]
    [InlineData(1000, 10, 200, 150, "bigger")]
    public void Arguments_that_cannot_make_a_field_are_named(int field, int count, int min, int max, string expected)
    {
        var error = AsteroidFieldSpawner.CheckArguments(field, count, min, max);
        if (expected == null) Assert.Null(error);
        else Assert.Contains(expected, error);
    }

    [Fact]
    public void Materials_are_trimmed_and_empty_or_repeated_ones_dropped()
    {
        Assert.Equal(new[] { "Iron_02", "Nickel_01" }, AsteroidFieldSpawner.ParseMaterials(" Iron_02, ,Nickel_01,iron_02,"));
        Assert.Empty(AsteroidFieldSpawner.ParseMaterials(null));
        Assert.Empty(AsteroidFieldSpawner.ParseMaterials(" , "));
    }

    [Theory]
    [InlineData(10, 64)]
    [InlineData(64, 64)]
    [InlineData(65, 128)]
    [InlineData(100, 128)]
    [InlineData(150, 256)]
    public void The_storage_is_the_size_rounded_up_to_a_power_of_two_and_at_least_64(double size, int edge)
    {
        Assert.Equal(edge, AsteroidFieldSpawner.StorageEdge(size));
    }

    [Fact]
    public void Nothing_of_the_storage_is_outside_its_extent()
    {
        // the corner of a 128 cube, from its centre
        Assert.True(AsteroidFieldSpawner.Extent(128) >= new Vector3D(64, 64, 64).Length() - 1e-9);
    }

    [Fact]
    public void A_position_is_inside_the_field_and_free()
    {
        var random = new Random(1);
        var centre = new Vector3D(1000, 2000, 3000);
        for (var i = 0; i < 200; i++)
        {
            Assert.True(AsteroidFieldSpawner.TryPickPosition(centre, 500, 50, random, s => s.Center.X > centre.X, out var at));
            Assert.True(Vector3D.Distance(at, centre) <= 500 + 1e-6);
            Assert.True(at.X > centre.X);
        }
    }

    [Fact]
    public void With_no_free_place_nothing_is_picked_after_the_tries()
    {
        var tries = 0;
        Assert.False(AsteroidFieldSpawner.TryPickPosition(Vector3D.Zero, 500, 50, new Random(1), s => { tries++; return false; }, out _));
        Assert.Equal(AsteroidFieldSpawner.PlacementTries, tries);
    }

    [Fact]
    public void The_candidate_sphere_has_the_asked_radius()
    {
        double seen = 0;
        AsteroidFieldSpawner.TryPickPosition(Vector3D.Zero, 500, 77, new Random(1), s => { seen = s.Radius; return true; }, out _);
        Assert.Equal(77, seen);
    }

    [Fact]
    public void A_planet_blocks_an_asteroid_that_would_reach_its_ground()
    {
        var planet = Vector3D.Zero;
        var ground = new Vector3D(0, 60000, 0);
        Assert.True(AsteroidFieldSpawner.ClearOfPlanet(new BoundingSphereD(new Vector3D(0, 61000, 0), 500), planet, ground));
        Assert.False(AsteroidFieldSpawner.ClearOfPlanet(new BoundingSphereD(new Vector3D(0, 60300, 0), 500), planet, ground));
        // under the ground
        Assert.False(AsteroidFieldSpawner.ClearOfPlanet(new BoundingSphereD(new Vector3D(0, 59000, 0), 10), planet, ground));
    }

    [Fact]
    public void An_id_taken_by_another_entity_is_not_given_again()
    {
        var first = AsteroidFieldSpawner.AsteroidEntityId("FieldAster-1r100");
        var taken = new HashSet<long> { first };
        var id = AsteroidFieldSpawner.UniqueEntityId("FieldAster-1r100", taken.Contains);
        Assert.NotEqual(first, id);
        Assert.Equal(first, AsteroidFieldSpawner.UniqueEntityId("FieldAster-1r100", _ => false));
    }

    [Fact]
    public void Asteroid_ids_are_of_the_asteroid_kind()
    {
        // the top byte of an entity id is its kind; the game's asteroids are 6
        foreach (var name in new[] { "a", "FieldAster-1r100", "FieldAster-1r100#1" })
            Assert.Equal(6, (int)((ulong)AsteroidFieldSpawner.AsteroidEntityId(name) >> 56));
    }

    [Fact]
    public void Stone_and_allowed_ores_stay_other_ores_turn_into_allowed_ones()
    {
        var allowed = new List<byte> { 10, 20 };
        var stone = new HashSet<byte> { 1, 2 };
        Assert.Equal(1, AsteroidFieldSpawner.MapMaterial(1, allowed, stone));
        Assert.Equal(20, AsteroidFieldSpawner.MapMaterial(20, allowed, stone));
        for (byte ore = 0; ore < 255; ore++)
        {
            var mapped = AsteroidFieldSpawner.MapMaterial(ore, allowed, stone);
            Assert.True(stone.Contains(mapped) || allowed.Contains(mapped));
            Assert.Equal(mapped, AsteroidFieldSpawner.MapMaterial(ore, allowed, stone));
        }
    }
}
