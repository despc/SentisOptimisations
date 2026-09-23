using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using SentisGameplayImprovements;
using Xunit;

namespace SentisOptimisations.Tests;

/// <summary>SentisGameplayImprovements' PCU limit: what happens to a group at each check.</summary>
public class PcuLimiterTests
{
    private const int Static = 200000, Dynamic = 30000;

    private static PcuLimiter.Verdict Decide(int pcu, bool hasStatic = false, bool enemyNear = true, int strikes = 0) =>
        PcuLimiter.Decide(pcu, hasStatic, enemyNear, strikes, Static, Dynamic);

    [Fact]
    public void Within_the_limit_nothing_happens()
    {
        var v = Decide(Dynamic);
        Assert.Equal(Dynamic, v.Limit);
        Assert.False(v.OverLimit);
        Assert.False(v.Enforce);
        Assert.Equal(0, v.Strikes);
        Assert.False(v.ConvertToStatic);
    }

    [Fact]
    public void Over_the_limit_with_an_enemy_near_the_blocks_go_off()
    {
        var v = Decide(Dynamic + 1);
        Assert.True(v.OverLimit);
        Assert.True(v.Enforce);
        Assert.Equal(1, v.Strikes);
        Assert.False(v.ConvertToStatic);
    }

    [Fact]
    public void With_no_enemy_near_the_owner_is_told_but_the_limit_is_enforced_only_past_the_grace()
    {
        var told = Decide(Dynamic + PcuLimiter.GraceWithoutEnemies, enemyNear: false);
        Assert.True(told.OverLimit);
        Assert.False(told.Enforce);
        Assert.Equal(0, told.Strikes);

        var enforced = Decide(Dynamic + PcuLimiter.GraceWithoutEnemies + 1, enemyNear: false);
        Assert.True(enforced.Enforce);
    }

    [Fact]
    public void A_group_with_a_static_grid_has_the_static_limit()
    {
        var v = Decide(Dynamic * 2, hasStatic: true);
        Assert.Equal(Static, v.Limit);
        Assert.False(v.OverLimit);
        Assert.True(Decide(Static + 1, hasStatic: true).Enforce);
    }

    [Fact]
    public void The_biggest_grid_goes_static_on_the_fifth_check_in_a_row_over_the_limit()
    {
        var strikes = 0;
        for (var check = 1; check < PcuLimiter.StrikesBeforeStatic; check++)
        {
            var v = Decide(Dynamic + 1, strikes: strikes);
            Assert.False(v.ConvertToStatic, "made static on check " + check);
            strikes = v.Strikes;
            Assert.Equal(check, strikes);
        }
        var last = Decide(Dynamic + 1, strikes: strikes);
        Assert.True(last.ConvertToStatic);
        Assert.Equal(0, last.Strikes);
    }

    [Fact]
    public void Back_within_the_limit_the_count_starts_over()
    {
        var v = Decide(Dynamic, strikes: PcuLimiter.StrikesBeforeStatic - 1);
        Assert.Equal(0, v.Strikes);
        Assert.False(v.ConvertToStatic);
        // within the grace with no enemy near counts as within the limit for the count
        Assert.Equal(0, Decide(Dynamic + 1, enemyNear: false, strikes: 3).Strikes);
    }

    [Fact]
    public void The_build_request_target_has_the_parameter_the_patch_names()
    {
        var method = typeof(MyCubeGrid).GetMethod("BuildBlocksRequest", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var locations = method.GetParameters().SingleOrDefault(p => p.Name == "locations");
        Assert.NotNull(locations);
        Assert.Equal(typeof(HashSet<MyCubeGrid.MyBlockLocation>), locations.ParameterType);
    }
}
