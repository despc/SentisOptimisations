using SentisGameplayImprovements.Explosions;
using SentisGameplayImprovements.Loot;
using VRageMath;
using Xunit;

namespace SentisOptimisations.Tests;

/// <summary>
/// The arithmetic behind SGI's explosions and loot: how far a blast reaches, when a crate goes
/// off, and how many components a hit knocks out of a block.
/// </summary>
public class ExplosionAndLootMathTests
{
    // ---------------------------------------------------------------- explosion falloff

    [Theory]
    [InlineData(23f, 0f, 23f)]
    [InlineData(24f, 0f, 23f)]
    [InlineData(30f, 10f, 23f)]
    public void Block_whose_centre_is_at_or_past_the_radius_takes_nothing(float distance, float travelled, float radius)
    {
        Assert.Equal(0f, ExplosionMath.Falloff(distance, travelled, radius));
    }

    [Fact]
    public void Block_at_the_centre_takes_everything()
    {
        Assert.Equal(1f, ExplosionMath.Falloff(0f, 0f, 20f));
    }

    [Fact]
    public void Falloff_is_linear_between_where_the_damage_came_from_and_the_edge()
    {
        Assert.Equal(0.5f, ExplosionMath.Falloff(10f, 0f, 20f), 4);
        Assert.Equal(0.5f, ExplosionMath.Falloff(15f, 10f, 20f), 4);
        Assert.Equal(1f, ExplosionMath.Falloff(5f, 10f, 20f));  // closer than the ray already came: clamped
    }

    [Fact]
    public void Damage_that_already_travelled_past_the_radius_does_not_divide_by_zero()
    {
        var share = ExplosionMath.Falloff(19f, 20f, 20f);
        Assert.False(float.IsNaN(share) || float.IsInfinity(share));
        Assert.Equal(1f, share);
    }

    [Fact]
    public void Falloff_never_grows_with_distance()
    {
        var previous = 1f;
        for (var d = 0f; d <= 25f; d += 0.25f)
        {
            var share = ExplosionMath.Falloff(d, 0f, 23f);
            Assert.InRange(share, 0f, previous);
            previous = share;
        }
    }

    // ---------------------------------------------------------------- going deeper

    [Fact]
    public void A_block_that_stands_stops_the_blast()
    {
        var rest = ExplosionMath.PassThrough(100f, 1f, 1f, 150f, out var dealt);
        Assert.Equal(100f, dealt);
        Assert.Equal(0f, rest);
    }

    [Fact]
    public void A_block_just_holding_on_stops_the_blast_too()
    {
        Assert.Equal(0f, ExplosionMath.PassThrough(100f, 1f, 1f, 100f, out _));
    }

    [Fact]
    public void A_destroyed_block_lets_the_rest_through()
    {
        var rest = ExplosionMath.PassThrough(100f, 0.5f, 1f, 20f, out var dealt);
        Assert.Equal(50f, dealt);
        Assert.Equal(30f, rest);
    }

    [Fact]
    public void What_goes_through_is_in_the_units_the_ray_carries()
    {
        // a block taking double damage: 100 arriving deals 200; 80 of it destroys the block,
        // the 120 left is 60 of what the ray carries
        var rest = ExplosionMath.PassThrough(100f, 1f, 2f, 80f, out var dealt);
        Assert.Equal(200f, dealt);
        Assert.Equal(60f, rest);
    }

    [Theory]
    [InlineData(0f, 1f, 1f)]
    [InlineData(100f, 0f, 1f)]
    [InlineData(100f, 1f, 0f)]
    [InlineData(-5f, 1f, 1f)]
    public void Nothing_arriving_deals_nothing_and_passes_nothing(float arriving, float share, float multiplier)
    {
        Assert.Equal(0f, ExplosionMath.PassThrough(arriving, share, multiplier, 10f, out var dealt));
        Assert.Equal(0f, dealt);
    }

    [Fact]
    public void A_ray_through_a_row_of_blocks_stops_at_the_first_that_stands()
    {
        // 10 armor blocks in a row, each destroyed by 30; 100 arriving at the first
        var arriving = 100f;
        var hit = 0;
        for (var i = 0; i < 10 && arriving > 0f; i++)
        {
            arriving = ExplosionMath.PassThrough(arriving, 1f, 1f, 30f, out var dealt);
            if (dealt > 0f) hit++;
        }
        Assert.Equal(4, hit);          // three destroyed (100 -> 70 -> 40 -> 10), the fourth takes 10 and stands
        Assert.Equal(0f, arriving);
    }

    // ---------------------------------------------------------------- crates going off

    [Theory]
    [InlineData(300f, 0f, 0f)]
    [InlineData(-300f, 0f, 0f)]
    [InlineData(0f, -300f, 0f)]
    [InlineData(0f, 0f, -300f)]
    [InlineData(150f, 150f, 150f)]  // no axis over the limit, the blow as a whole is
    public void Crate_goes_off_when_knocked_hard_from_any_side(float x, float y, float z)
    {
        Assert.True(ExplosionMath.ShouldDetonate(new Vector3(x, y, z), 200f));
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(-9.81f, 0f, 0f)]
    [InlineData(100f, -100f, 100f)]
    public void Crate_stays_put_when_only_nudged(float x, float y, float z)
    {
        Assert.False(ExplosionMath.ShouldDetonate(new Vector3(x, y, z), 200f));
    }

    [Fact]
    public void No_limit_means_crates_never_go_off_by_themselves()
    {
        Assert.False(ExplosionMath.ShouldDetonate(new Vector3(1e6f, 0, 0), 0f));
    }

    [Fact]
    public void Ammo_damage_is_per_round_with_a_fallback_for_zero()
    {
        Assert.Equal(20f * 2f * 50, ExplosionMath.AmmoDamage(20f, 30f, 2f, 50));
        Assert.Equal(30f * 2f * 50, ExplosionMath.AmmoDamage(0f, 30f, 2f, 50));
        Assert.Equal(0f, ExplosionMath.AmmoDamage(20f, 30f, 2f, -5));
    }

    // ---------------------------------------------------------------- loot

    // A stack of 10 components worth 100 integrity: 10 per component.
    private static LootMath.Stack Full(int count = 10, float perComponent = 10f) =>
        new LootMath.Stack(count * perComponent, count, count, count * perComponent);

    [Fact]
    public void No_damage_loses_nothing()
    {
        Assert.Equal(new[] { 0 }, LootMath.Lost(new[] { Full() }, 0f));
        Assert.Equal(new[] { 0 }, LootMath.Lost(new[] { Full() }, -5f));
    }

    [Fact]
    public void A_scratch_loses_nothing()
    {
        Assert.Equal(new[] { 0 }, LootMath.Lost(new[] { Full() }, 3f));
    }

    [Fact]
    public void Damage_of_exactly_whole_components_loses_exactly_that_many()
    {
        // the old count kept one component too many here
        Assert.Equal(new[] { 3 }, LootMath.Lost(new[] { Full() }, 30f));
    }

    [Fact]
    public void Part_of_a_component_left_keeps_that_component()
    {
        Assert.Equal(new[] { 2 }, LootMath.Lost(new[] { Full() }, 25f));
    }

    [Fact]
    public void Emptying_a_stack_loses_all_of_it()
    {
        Assert.Equal(new[] { 10 }, LootMath.Lost(new[] { Full() }, 100f));
        Assert.Equal(new[] { 10 }, LootMath.Lost(new[] { Full() }, 1000f));
    }

    [Fact]
    public void Damage_eats_the_last_stack_first_and_carries_over()
    {
        var stacks = new[] { Full(4, 25f), Full(10, 10f) };  // steel plates below, a panel on top
        Assert.Equal(new[] { 0, 5 }, LootMath.Lost(stacks, 50f));
        Assert.Equal(new[] { 2, 10 }, LootMath.Lost(stacks, 150f));
    }

    [Fact]
    public void A_damaged_stack_loses_only_what_it_still_had()
    {
        // 10 components, integrity down to 45: five left, the fifth only half there
        var stack = new LootMath.Stack(45f, 5, 10, 100f);
        Assert.Equal(new[] { 0 }, LootMath.Lost(new[] { stack }, 0.1f));
        Assert.Equal(new[] { 1 }, LootMath.Lost(new[] { stack }, 5f));
        Assert.Equal(new[] { 5 }, LootMath.Lost(new[] { stack }, 45f));
    }

    [Fact]
    public void Stacks_with_nothing_mounted_or_no_integrity_are_skipped()
    {
        var empty = new LootMath.Stack(0f, 0, 10, 100f);
        var broken = new LootMath.Stack(10f, 1, 0, 0f);
        Assert.Equal(new[] { 3, 0, 0 }, LootMath.Lost(new[] { Full(), empty, broken }, 30f));
    }

    [Fact]
    public void Lost_never_exceeds_what_is_mounted()
    {
        for (var damage = 0f; damage < 300f; damage += 1.7f)
        {
            var lost = LootMath.Lost(new[] { Full(4, 25f), Full(10, 10f) }, damage);
            Assert.InRange(lost[0], 0, 4);
            Assert.InRange(lost[1], 0, 10);
        }
    }

    [Theory]
    [InlineData(10, 0.5f, 5)]
    [InlineData(10, 1f, 10)]
    [InlineData(10, 2f, 10)]
    [InlineData(10, 0f, 0)]
    [InlineData(0, 1f, 0)]
    [InlineData(-3, 1f, 0)]
    [InlineData(3, 0.3f, 0)]
    public void Dropped_is_the_drop_chance_of_what_was_lost(int lost, float probability, int expected)
    {
        Assert.Equal(expected, LootMath.Dropped(lost, probability));
    }
}
