using System.Reflection;
using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests;

/// <summary>The wind turbine's ray throttle: what it patches and how often it lets a turbine cast.</summary>
public class WindTurbineRaysTests
{
    [Fact]
    public void The_turbine_casts_its_rays_in_UpdateNextRay()
    {
        var nextRay = typeof(SpaceEngineers.Game.Entities.Blocks.MyWindTurbine).GetMethod("UpdateNextRay", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(nextRay);
        Assert.NotNull(typeof(SpaceEngineers.Game.Entities.Blocks.MyWindTurbine).GetProperty("RayEffectivities"));
        // one ray in flight at a time: the throttle counts only the calls that cast
        var running = typeof(SpaceEngineers.Game.Entities.Blocks.MyWindTurbine).GetField("m_paralleRaycastRunning", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(running);
        Assert.Equal(typeof(bool), running.FieldType);
    }

    [Fact]
    public void A_new_turbine_casts_its_first_round_at_once()
    {
        var state = WindTurbineRays.NewState();
        for (var i = 0; i < 9; i++) Assert.True(WindTurbineRays.Due(state, 1000UL + (ulong)i, WindTurbineRays.SeenPeriod, 9));
        Assert.False(WindTurbineRays.Due(state, 1009UL, WindTurbineRays.SeenPeriod, 9));
    }

    [Fact]
    public void Then_once_a_period()
    {
        var state = WindTurbineRays.NewState();
        for (var i = 0; i < 9; i++) WindTurbineRays.Due(state, 10UL, WindTurbineRays.SeenPeriod, 9);
        Assert.False(WindTurbineRays.Due(state, 10UL + WindTurbineRays.SeenPeriod - 1, WindTurbineRays.SeenPeriod, 9));
        Assert.True(WindTurbineRays.Due(state, 10UL + WindTurbineRays.SeenPeriod, WindTurbineRays.SeenPeriod, 9));
        Assert.False(WindTurbineRays.Due(state, 10UL + WindTurbineRays.SeenPeriod + 1, WindTurbineRays.SeenPeriod, 9));
    }

    [Fact]
    public void A_full_round_stays_within_seconds()
    {
        // nine rays: 9 s where a player sees the turbine, 45 s where none does
        Assert.InRange(WindTurbineRays.SeenPeriod * 9 / 60, 1, 10);
        Assert.InRange(WindTurbineRays.IdlePeriod * 9 / 60, 10, 60);
    }
}

/// <summary>What the idle character's range-ray skip patches and reads.</summary>
public class IdleCharacterRangeTests
{
    [Fact]
    public void The_character_casts_its_range_ray_in_UpdateDynamicRange()
    {
        var update = typeof(Sandbox.Game.Entities.Character.MyCharacter).GetMethod("UpdateDynamicRange", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(update);
        Assert.Empty(update.GetParameters());
        Assert.NotNull(typeof(Sandbox.Game.Entities.Character.MyCharacter).GetProperty("ControllerInfo"));
    }
}
