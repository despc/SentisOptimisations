using System;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Blocks;
using SentisOptimisationsPlugin.Freezer;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.EntityComponents.GameLogic;
using Xunit;

namespace SentisOptimisations.Tests
{
    /// <summary>
    /// The catch-up a grid gets for the time it spent frozen: gas generators, oxygen farms and the
    /// plants in a farm plot. The arithmetic is tested directly; what the game has to provide for
    /// the rest of it to work is asserted structurally, so a game update that moves a member is
    /// caught here rather than by a farm that silently stops being compensated.
    /// </summary>
    public class FrozenProductionTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // ---------------------------------------------------------------- how long it was frozen

        [Fact]
        public void A_freeze_is_worth_the_frames_it_lasted()
        {
            Assert.Equal(6_000UL, FrozenProduction.Rules.FrozenFrames(1_000, 7_000));
        }

        [Fact]
        public void A_stamp_from_the_future_is_worth_nothing()
        {
            // A deleted block's EntityId comes back to a new block; its leftover stamp must not
            // wrap around into hours of free production.
            Assert.Equal(0UL, FrozenProduction.Rules.FrozenFrames(9_000, 1_000));
        }

        [Fact]
        public void Nothing_is_paid_beyond_the_sanity_cap()
        {
            const ulong cap = 1_000;
            Assert.Equal(cap, FrozenProduction.Rules.FrozenFrames(0, 10_000_000, cap));
        }

        // ---------------------------------------------------------------- the sun over a freeze

        [Fact]
        public void A_short_freeze_is_paid_by_the_sun_at_its_ends()
        {
            Assert.Equal(0.6, FrozenProduction.Rules.SunFactor(60, 0.4, 0.8, twoSided: false), 3);
        }

        [Fact]
        public void A_long_freeze_is_paid_by_a_whole_days_average()
        {
            // Frozen at noon and thawed at noon a day later: a panel that saw the night too.
            var factor = FrozenProduction.Rules.SunFactor(24 * 3600, 1, 1, twoSided: false);
            Assert.Equal(1 / Math.PI, factor, 4);
            Assert.True(factor < 1, "an overnight freeze must not pay out a permanent noon");
        }

        [Fact]
        public void A_panel_lit_from_both_sides_collects_twice_as_much_over_a_day()
        {
            Assert.Equal(2 * FrozenProduction.Rules.SunFactor(24 * 3600, 1, 1, twoSided: false),
                FrozenProduction.Rules.SunFactor(24 * 3600, 1, 1, twoSided: true), 4);
        }

        [Fact]
        public void No_time_frozen_means_no_sun()
        {
            Assert.Equal(0, FrozenProduction.Rules.SunFactor(0, 1, 1, twoSided: false));
        }

        // ---------------------------------------------------------------- at what rate

        [Fact]
        public void A_block_carries_on_at_the_rate_it_was_working_at()
        {
            // Rated for 100 a second but only asked for 30 when it froze: 30 a second, not 100.
            Assert.Equal(300, FrozenProduction.Rules.Wanted(ratedPerSecond: 100, deliveredPerSecond: 30, seconds: 10));
        }

        [Fact]
        public void And_never_faster_than_it_is_rated_for()
        {
            Assert.Equal(1000, FrozenProduction.Rules.Wanted(ratedPerSecond: 100, deliveredPerSecond: 250, seconds: 10));
        }

        [Fact]
        public void A_block_that_was_making_nothing_is_owed_nothing()
        {
            // Freezing a base must not be worth more than living in it.
            Assert.Equal(0, FrozenProduction.Rules.Wanted(100, 0, 3600));
            Assert.Equal(0, FrozenProduction.Rules.Wanted(100, 30, 0));
        }

        // ---------------------------------------------------------------- what could be produced

        [Fact]
        public void A_generator_is_paid_what_it_is_rated_for()
        {
            Assert.Equal(100, FrozenProduction.Rules.Producible(wanted: 100, tankRoom: 500, iceAvailable: 500, iceToGasRatio: 4));
        }

        [Fact]
        public void No_more_than_the_tanks_had_room_for()
        {
            Assert.Equal(30, FrozenProduction.Rules.Producible(wanted: 100, tankRoom: 30, iceAvailable: 500, iceToGasRatio: 4));
        }

        [Fact]
        public void No_more_than_the_ice_would_have_turned_into()
        {
            // Two kilograms of ice at four gas each: eight, whatever the rating says.
            Assert.Equal(8, FrozenProduction.Rules.Producible(wanted: 100, tankRoom: 500, iceAvailable: 2, iceToGasRatio: 4));
        }

        [Fact]
        public void Without_ice_or_room_nothing_is_produced()
        {
            Assert.Equal(0, FrozenProduction.Rules.Producible(100, 0, 500, 4));
            Assert.Equal(0, FrozenProduction.Rules.Producible(100, 500, 0, 4));
            Assert.Equal(0, FrozenProduction.Rules.Producible(0, 500, 500, 4));
        }

        // ---------------------------------------------------------------- fuel for the charge

        [Fact]
        public void A_producer_is_charged_fuel_for_the_energy_it_made()
        {
            // 12 MW for half an hour is 6 MWh, and 6 MWh at 12 MW took half an hour.
            Assert.Equal(6, FrozenProduction.Rules.EnergyMwh(12, 1800), 6);
            Assert.Equal(1800, FrozenProduction.Rules.SecondsFor(6, 12), 6);
        }

        [Fact]
        public void A_reactor_burns_its_rated_share()
        {
            // Half throttle, one item a second, ten seconds: five items.
            Assert.Equal(5, FrozenProduction.Rules.FuelBurned(FrozenProduction.Rules.OutputShare(6, 12), 1, 10), 6);
        }

        [Fact]
        public void An_engine_burns_its_output_divided_by_what_the_fuel_is_worth()
        {
            Assert.Equal(50, FrozenProduction.Rules.GasBurned(output: 5, productionToCapacityMultiplier: 0.5, seconds: 5), 6);
        }

        [Fact]
        public void A_producer_that_was_idle_is_charged_nothing()
        {
            Assert.Equal(0, FrozenProduction.Rules.OutputShare(0, 12));
            Assert.Equal(0, FrozenProduction.Rules.FuelBurned(0, 1, 10));
            Assert.Equal(0, FrozenProduction.Rules.GasBurned(0, 0.5, 10));
            Assert.Equal(0, FrozenProduction.Rules.EnergyMwh(12, 0));
        }

        [Fact]
        public void An_overdriven_reading_is_still_only_full_throttle()
        {
            Assert.Equal(1, FrozenProduction.Rules.OutputShare(20, 12));
        }

        // ---------------------------------------------------------------- what the game must provide

        [Fact]
        public void The_generator_still_says_what_it_makes_and_what_it_burns()
        {
            Assert.NotNull(typeof(MyOxygenGeneratorDefinition).GetField(nameof(MyOxygenGeneratorDefinition.IceConsumptionPerSecond)));
            Assert.NotNull(typeof(MyOxygenGeneratorDefinition).GetField(nameof(MyOxygenGeneratorDefinition.ProducedGases)));
            var gas = typeof(MyOxygenGeneratorDefinition.MyGasGeneratorResourceInfo);
            Assert.NotNull(gas.GetField("Id"));
            Assert.NotNull(gas.GetField("IceToGasRatio"));
        }

        [Fact]
        public void The_farm_still_says_what_it_makes_and_how_it_faces_the_sun()
        {
            Assert.NotNull(typeof(MyOxygenFarmDefinition).GetField(nameof(MyOxygenFarmDefinition.MaxGasOutput)));
            Assert.NotNull(typeof(MyOxygenFarmDefinition).GetField(nameof(MyOxygenFarmDefinition.IsTwoSided)));
            Assert.NotNull(typeof(MyOxygenFarmDefinition).GetField(nameof(MyOxygenFarmDefinition.ProducedGas)));
            Assert.NotNull(typeof(MyOxygenFarm).GetProperty(nameof(MyOxygenFarm.SolarComponent), Any));
        }

        [Fact]
        public void A_tank_can_still_be_filled_and_asked_how_full_it_is()
        {
            // The setter is private; the ModAPI interface is the way in.
            var change = typeof(Sandbox.ModAPI.IMyGasTank).GetMethod("ChangeFilledRatio");
            Assert.NotNull(change);
            Assert.Equal(new[] { typeof(double), typeof(bool) }, change.GetParameters().Select(p => p.ParameterType));
            Assert.NotNull(typeof(MyGasTank).GetProperty(nameof(MyGasTank.Capacity)));
            Assert.NotNull(typeof(MyGasTank).GetProperty(nameof(MyGasTank.FilledRatio)));
        }

        [Fact]
        public void A_reactor_and_an_engine_still_say_what_they_burn()
        {
            Assert.NotNull(typeof(Sandbox.Definitions.MyReactorDefinition).GetField(
                nameof(Sandbox.Definitions.MyReactorDefinition.FuelInfos)));
            var fuel = typeof(Sandbox.Definitions.MyReactorDefinition.FuelInfo);
            Assert.NotNull(fuel.GetField("FuelId"));
            Assert.NotNull(fuel.GetField("ConsumptionPerSecond_Items"));

            var engine = typeof(Sandbox.Definitions.MyGasFueledPowerProducerDefinition);
            Assert.NotNull(engine.GetField(nameof(Sandbox.Definitions.MyGasFueledPowerProducerDefinition.Fuel)));
            Assert.NotNull(engine.GetField(nameof(Sandbox.Definitions.MyGasFueledPowerProducerDefinition.FuelProductionToCapacityMultiplier)));

            // The engine's own fuel buffer is drained before its tanks are.
            var capacity = typeof(Sandbox.Game.Entities.MyFueledPowerProducer).GetProperty("Capacity");
            Assert.NotNull(capacity);
            Assert.True(capacity.CanWrite, "the engine's fuel buffer cannot be drained");

            // And the battery, whose room is what the fuel is measured against.
            Assert.NotNull(typeof(Sandbox.Game.Entities.MyBatteryBlock).GetProperty(
                nameof(Sandbox.Game.Entities.MyBatteryBlock.MaxStoredPower)));
            Assert.NotNull(typeof(Sandbox.Game.Entities.MyBatteryBlock).GetProperty(
                nameof(Sandbox.Game.Entities.MyBatteryBlock.CurrentStoredPower)));
        }

        [Fact]
        public void Power_is_still_shared_across_the_logical_group()
        {
            // Which grids count towards the battery room a reactor is charged fuel for: the ones
            // that share the resource distributor, and that is what the logical group owns. If the
            // game ever moves it, the budget would stop seeing the batteries on a subgrid.
            var data = typeof(Sandbox.Game.Entities.MyGridLogicalGroupData);
            var distributor = data.GetField("ResourceDistributor", Any) ?? data.GetProperty("ResourceDistributor", Any) as MemberInfo;
            Assert.NotNull(distributor);
        }

        [Fact]
        public void A_plant_can_still_be_stopped_and_stepped_forward()
        {
            // Freezing a plot means clearing its own update flag - the freezer's unregistration of
            // the block does not reach a component - and the catch-up is vanilla's own 100-frame
            // update run again, so water, frost and growth stages stay exactly as vanilla has them.
            var needsUpdate = typeof(MyFarmPlotLogic).GetProperty("NeedsUpdate");
            Assert.NotNull(needsUpdate);
            Assert.True(needsUpdate.CanWrite, "a plot that cannot be stopped keeps growing on a frozen grid");

            var update = typeof(MyFarmPlotLogic).GetMethod(nameof(MyFarmPlotLogic.UpdateAfterSimulation100),
                BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            Assert.NotNull(update);
            Assert.NotNull(typeof(MyFarmPlotLogic).GetProperty(nameof(MyFarmPlotLogic.IsPlantPlanted)));
            Assert.NotNull(typeof(MyFarmPlotLogic).GetProperty(nameof(MyFarmPlotLogic.IsAlive)));
        }

        [Fact]
        public void The_freezer_hands_its_grids_over_on_both_ends()
        {
            // Stamped when the grid stops updating, paid out when it starts again; a missing call
            // on either side is a silent loss of everything the grid should have made.
            var source = FreezeLogicSource();
            Assert.Contains("FrozenProduction.OnFrozen(grid, frame)", source);
            Assert.Contains("FrozenProduction.OnThawed(grid, frame)", source);
            Assert.Contains("FrozenProduction.Forget(block.EntityId)", source);
        }

        /// <summary>The file itself, found by walking up from the test binaries to the repository.</summary>
        private static string FreezeLogicSource()
        {
            var directory = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var candidate = System.IO.Path.Combine(directory.FullName, "SentisOptimisations", "Freezer", "FreezeLogic.cs");
                if (System.IO.File.Exists(candidate)) return System.IO.File.ReadAllText(candidate);
                directory = directory.Parent;
            }

            throw new Xunit.Sdk.XunitException("FreezeLogic.cs not found above " + AppDomain.CurrentDomain.BaseDirectory);
        }
    }
}
