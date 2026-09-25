using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.Game.World;
using VRage.Game.ModAPI;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.EntityComponents.GameLogic;
using VRage;
using VRage.Game;

namespace SentisOptimisationsPlugin.Freezer;

/// <summary>
/// What a frozen grid would have made while nobody was looking, handed to it when it thaws.
///
/// The freezer already compensates assemblers and refineries: their frozen frames go back into the
/// vanilla production timer (see FreezeLogic.CompensateFrozenFrames). Three other blocks quietly
/// make things over time and are not production blocks, so nothing covered them:
///
///  * an O2/H2 generator turning ice into gas,
///  * an oxygen farm turning sunlight into oxygen,
///  * a farm plot growing a plant.
///
/// The first two are handed the gas they would have made, limited by what could really have
/// happened: the ice that was actually there, and the room the tanks on their conveyor network
/// actually had. The plant is different - its growth lives in a component the freezer's
/// unregistration does not reach, so it kept growing (and drinking, and dying of frost) on a grid
/// that was frozen solid. It is stopped here with the rest of the grid and worked forward on the
/// thaw by running the vanilla 100-frame update the number of times it was missed, which keeps
/// water, temperature and growth stages exactly as vanilla would have had them.
///
/// Everything is capped at <see cref="MaxCatchUpFrames"/>, the same sanity cap the timer
/// compensation uses: a stale stamp can never turn into days of free hydrogen.
/// </summary>
public static class FrozenProduction
{
    /// <summary>Sanity cap for one catch-up: the timer compensation's.</summary>
    private const ulong MaxCatchUpFrames = FreezeLogic.MaxCompensationFrames;

    /// <summary>Below this the sun is averaged over the freeze itself; above it, over a whole day.</summary>
    private const double ShortFreezeSeconds = 600;

    /// <summary>
    /// A flat panel facing the sky collects the average of max(0, cos t) over a full turn of the
    /// sun - 1/pi of its rated output, twice that when the panel takes light on both sides. What a
    /// grid frozen overnight gets is a day's worth of oxygen, not a permanent noon.
    /// </summary>
    private const double DayAverageSun = 1 / Math.PI;

    /// <summary>Plant catch-up ticks run per frame, all plots together: a spread-out fast-forward.</summary>
    private const int PlotTicksPerFrame = 40;

    private const int FramesPerPlotTick = 100;
    private const double FramesPerSecond = 60;

    private sealed class Stamp
    {
        public ulong Frame;
        public float Sun;

        /// <summary>
        /// What the block was actually delivering per second when it stopped, by gas. A generator
        /// is rated far above what it usually gets asked for, and paying the rating would make
        /// parking a ship in the cold more profitable than flying it.
        /// </summary>
        public Dictionary<MyDefinitionId, float> Output;

        /// <summary>
        /// The electricity a reactor or an engine was putting out when it stopped. Its battery
        /// carries on charging over the freeze all by itself - vanilla works the stored charge out
        /// from a timestamp - so the fuel for that charge has to be taken as well, or a base left
        /// out in the cold charges almost for free.
        /// </summary>
        public float Power;
    }

    private sealed class PlotWork
    {
        public MyCubeBlock Block;
        public MyFarmPlotLogic Logic;
        public int TicksLeft;
    }

    // blockId -> the frame at which it stopped updating
    private static readonly ConcurrentDictionary<long, Stamp> Stamps = new();

    // plots being fast-forwarded, and what is left over for a plot frozen again mid-catch-up
    private static readonly ConcurrentQueue<PlotWork> PlotQueue = new();
    private static readonly ConcurrentDictionary<long, int> PlotTicksPending = new();

    private static long _gasCompensated;
    private static long _plotTicks;
    private static double _fuelBurned;

    /// <summary>For the compensation log (see EnableCompensationLogs).</summary>
    public static string Summary() =>
        $"Compensated: {_gasCompensated / 1000} k gas, {_fuelBurned:F0} fuel burned, {_plotTicks} plant ticks" +
        (PlotQueue.IsEmpty ? "" : $" ({PlotQueue.Count} plants catching up)");


    /// <summary>
    /// The arithmetic of the catch-up, with no game types in it: how long a freeze really lasted,
    /// how much sun a farm saw over it, and how much gas a generator could truly have made.
    /// </summary>
    public static class Rules
    {
        /// <summary>
        /// How long the block really was frozen, in frames. A stamp from the future belongs to a
        /// deleted block whose EntityId was handed out again - nothing is owed for that - and
        /// anything past the cap is not worth trusting.
        /// </summary>
        public static ulong FrozenFrames(ulong since, ulong now, ulong cap = MaxCatchUpFrames)
        {
            if (since > now) return 0;
            var frames = now - since;
            return frames > cap ? cap : frames;
        }

        /// <summary>
        /// The share of its rated output a farm saw. Over a short freeze the sun barely moved, so
        /// the readings at both ends of the window describe it; over a long one it went round, and
        /// a whole day's average is the honest answer - an overnight freeze must not pay out noon.
        /// </summary>
        public static double SunFactor(double seconds, double atFreeze, double atThaw, bool twoSided)
        {
            if (seconds <= 0) return 0;
            if (seconds <= ShortFreezeSeconds) return Math.Max(0, (atFreeze + atThaw) / 2);
            return DayAverageSun * (twoSided ? 2 : 1);
        }

        /// <summary>
        /// What the block would have made over the period. Not its rating: what it was actually
        /// delivering when it stopped. A generator rated for forty kilograms of ice a second that
        /// was quietly topping up a tank at half that rate carries on at half - otherwise leaving a
        /// base frozen would produce more hydrogen than living in it.
        /// </summary>
        public static double Wanted(double ratedPerSecond, double deliveredPerSecond, double seconds)
        {
            if (seconds <= 0) return 0;
            var rate = Math.Min(Math.Max(0, ratedPerSecond), Math.Max(0, deliveredPerSecond));
            return rate * seconds;
        }

        /// <summary>The energy, in MWh, a producer running at that many MW makes over the period.</summary>
        public static double EnergyMwh(double megawatts, double seconds) =>
            megawatts <= 0 || seconds <= 0 ? 0 : megawatts * seconds / 3600;

        /// <summary>And back again: how long a producer had to run at that rate to make it.</summary>
        public static double SecondsFor(double megawattHours, double megawatts) =>
            megawattHours <= 0 || megawatts <= 0 ? 0 : megawattHours * 3600 / megawatts;

        /// <summary>The share of its rating a power producer was running at, 0..1.</summary>
        public static double OutputShare(double output, double maxOutput)
        {
            if (output <= 0 || maxOutput <= 0) return 0;
            return Math.Min(1, output / maxOutput);
        }

        /// <summary>
        /// The solid fuel a reactor burns over the period, by vanilla's own arithmetic: the share of
        /// its rating it was running at, times its consumption, times the time.
        /// </summary>
        public static double FuelBurned(double outputShare, double consumptionPerSecond, double seconds) =>
            outputShare <= 0 || consumptionPerSecond <= 0 || seconds <= 0
                ? 0
                : outputShare * consumptionPerSecond * seconds;

        /// <summary>The gas an engine burns over the period: its output divided by what a unit of fuel is worth.</summary>
        public static double GasBurned(double output, double productionToCapacityMultiplier, double seconds) =>
            output <= 0 || productionToCapacityMultiplier <= 0 || seconds <= 0
                ? 0
                : output / productionToCapacityMultiplier * seconds;

        /// <summary>
        /// What the generator could really have produced: never more than it is rated for, than the
        /// tanks had room for, or than the ice within its reach would have turned into.
        /// </summary>
        public static double Producible(double wanted, double tankRoom, double iceAvailable, double iceToGasRatio)
        {
            if (wanted <= 0 || tankRoom <= 0 || iceAvailable <= 0 || iceToGasRatio <= 0) return 0;
            return Math.Min(Math.Min(wanted, tankRoom), iceAvailable * iceToGasRatio);
        }
    }

    // ------------------------------------------------------------------ freeze / thaw

    /// <summary>On the game thread, at the frame the grid stops updating.</summary>
    public static void OnFrozen(MyCubeGrid grid, ulong frame)
    {
        try
        {
            foreach (var block in grid.GetFatBlocks())
            {
                if (block is MyGasGenerator generator)
                {
                    Stamps[generator.EntityId] = new Stamp { Frame = frame, Output = Output(generator.SourceComp) };
                    continue;
                }

                if (block is MyOxygenFarm farm)
                {
                    Stamps[farm.EntityId] = new Stamp
                    {
                        Frame = frame, Sun = SunFactor(farm), Output = Output(farm.SourceComp)
                    };
                    continue;
                }

                if (block is MyFueledPowerProducer producer)
                {
                    Stamps[producer.EntityId] = new Stamp { Frame = frame, Power = producer.SourceComp.CurrentOutput };
                    continue;
                }

                // A plant grows in a component of its own, which keeps its own update registration
                // and re-arms it whenever the block's state changes - clearing the flag here does
                // not hold. The stamp is what stops it: FarmPlotFreeze skips the growth while it
                // is there (and it is what the catch-up is measured from).
                if (FarmPlot(block) != null) Stamps[block.EntityId] = new Stamp { Frame = frame };
            }
        }
        catch (Exception e)
        {
            SentisOptimisationsPlugin.Log.Error(e, "Freezing the production of " + grid.DisplayName + " failed");
        }
    }

    /// <summary>On the game thread, as the grid starts updating again.</summary>
    public static void OnThawed(MyCubeGrid grid, ulong frame)
    {
        try
        {
            if (Stamps.IsEmpty) return;
            Network network = null;

            foreach (var block in grid.GetFatBlocks())
            {
                if (!Stamps.TryRemove(block.EntityId, out var stamp)) continue;
                var frames = FrozenFrames(block.EntityId, stamp.Frame, frame);
                var seconds = frames / FramesPerSecond;

                var plot = FarmPlot(block);
                if (plot != null)
                {
                    QueuePlot(block, plot, (int)(frames / FramesPerPlotTick));
                    continue;
                }

                if (seconds <= 0) continue;
                network = network ?? Network.For(grid, frame);
                if (block is MyGasGenerator generator) CompensateGenerator(generator, seconds, stamp, network);
                else if (block is MyOxygenFarm farm) CompensateFarm(farm, seconds, stamp, network);
                else if (block is MyReactor reactor) CompensateReactor(reactor, seconds, stamp, network);
                else if (block is MyGasFueledPowerProducer engine) CompensateEngine(engine, seconds, stamp, network);
            }
        }
        catch (Exception e)
        {
            SentisOptimisationsPlugin.Log.Error(e, "Compensating the production of " + grid.DisplayName + " failed");
        }
    }

    /// <summary>Every frame, from the plugin: works the plants' backlog off a slice at a time.</summary>
    public static void Tick()
    {
        var budget = PlotTicksPerFrame;
        while (budget > 0 && PlotQueue.TryDequeue(out var work))
        {
            if (work.Block.Closed || work.Block.MarkedForClose) continue;

            // Frozen again: keep what is left for the next thaw instead of running it now.
            if (Stamps.ContainsKey(work.Block.EntityId))
            {
                Keep(work.Block.EntityId, work.TicksLeft);
                continue;
            }

            var ticks = Math.Min(budget, work.TicksLeft);
            for (var i = 0; i < ticks; i++)
            {
                try
                {
                    work.Logic.UpdateAfterSimulation100();
                }
                catch (Exception e)
                {
                    SentisOptimisationsPlugin.Log.Error(e, "A plant's catch-up failed");
                    work.TicksLeft = 0;
                    break;
                }
            }

            budget -= ticks;
            _plotTicks += ticks;
            work.TicksLeft -= ticks;
            if (work.TicksLeft > 0) PlotQueue.Enqueue(work);
        }
    }

    // ------------------------------------------------------------------ the gas blocks

    /// <summary>
    /// What a block could have reached: the tanks, the fuel and the battery room around it.
    ///
    /// The scope is the logical group, not one grid - that is the group that owns the resource
    /// distributor, so a rotor or a connector makes one power network and one conveyor network out
    /// of several grids, and a reactor on the hull charges the batteries on a subgrid. Inside it the
    /// blocks are grouped by the conveyor network they sit on, so a station with four hundred
    /// generators asks about connectivity four hundred times, not four hundred times per container.
    ///
    /// One is built per group per frame: a whole group thaws in a single pass, and rebuilding it for
    /// each of its grids would walk the same blocks again and again.
    /// </summary>
    private sealed class Network
    {
        private static readonly List<MyCubeGrid> GroupNodes = new();
        private static readonly Dictionary<long, Network> Built = new();
        private static ulong _builtAtFrame;

        /// <summary>The network around this grid, built once per group per frame.</summary>
        public static Network For(MyCubeGrid grid, ulong frame)
        {
            if (frame != _builtAtFrame)
            {
                Built.Clear();
                _builtAtFrame = frame;
            }

            var grids = GridsAround(grid);
            var key = grids.Count == 0 ? grid.EntityId : grids.Min(g => g.EntityId);
            if (Built.TryGetValue(key, out var known)) return known;
            var network = new Network(grids);
            Built[key] = network;
            return network;
        }

        /// <summary>Everything wired to this grid: its logical group, or the grid alone.</summary>
        private static List<MyCubeGrid> GridsAround(MyCubeGrid grid)
        {
            try
            {
                GroupNodes.Clear();
                MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Logical).GetGroupNodes(grid, GroupNodes);
                var grids = GroupNodes.Where(g => g != null && !g.MarkedForClose).ToList();
                GroupNodes.Clear();
                return grids.Count == 0 ? new List<MyCubeGrid> { grid } : grids;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "Reading the grid group of " + grid.DisplayName + " failed");
                return new List<MyCubeGrid> { grid };
            }
        }

        private sealed class Group
        {
            public MyCubeBlock Representative;
            public List<MyGasTank> Tanks;
            public List<MyInventory> Inventories;
        }

        private readonly List<MyGasTank> _tanks = new();
        private readonly List<MyInventory> _inventories = new();
        private readonly List<Group> _groups = new();

        /// <summary>
        /// The energy, in MWh, the power network can still take in - the room left in the batteries
        /// of the whole group. Vanilla charges a battery over a freeze from a timestamp, but never
        /// past its capacity, so this is the most the frozen period can have put into the network.
        /// Fuel is burned for that and not a watt more: a base whose batteries were already full
        /// spends nothing.
        /// </summary>
        public double EnergyBudget { get; private set; }

        private Network(List<MyCubeGrid> grids)
        {
            foreach (var block in grids.SelectMany(g => g.GetFatBlocks()))
            {
                if (block is MyGasTank tank)
                {
                    _tanks.Add(tank);
                    continue;
                }

                if (block is MyBatteryBlock battery && battery.Enabled &&
                    ((Sandbox.ModAPI.Ingame.IMyBatteryBlock)battery).ChargeMode != Sandbox.ModAPI.Ingame.ChargeMode.Discharge)
                    EnergyBudget += Math.Max(0, battery.MaxStoredPower - battery.CurrentStoredPower);

                if (!block.HasInventory) continue;
                for (var i = 0; i < block.InventoryCount; i++)
                    if (block.GetInventory(i) is MyInventory inventory)
                        _inventories.Add(inventory);
            }
        }

        /// <summary>Tanks of that gas the block can push into, and how much they would take now.</summary>
        public List<MyGasTank> TanksFor(MyCubeBlock from, MyDefinitionId gas, out double room)
        {
            var reachable = new List<MyGasTank>();
            room = 0;
            foreach (var tank in GroupFor(from).Tanks)
            {
                if (tank.BlockDefinition.StoredGasId != gas || !tank.IsWorking) continue;
                var free = (1 - tank.FilledRatio) * tank.Capacity;
                if (free <= 0) continue;
                reachable.Add(tank);
                room += free;
            }

            return reachable;
        }

        /// <summary>
        /// Books energy against the budget and says how much of it was there to book. The producers
        /// of one power network share one budget, so two reactors charging one battery bank do not
        /// both get paid for filling it.
        /// </summary>
        public double TakeEnergy(double megawattHours)
        {
            var taken = Math.Min(Math.Max(0, megawattHours), EnergyBudget);
            EnergyBudget -= taken;
            return taken;
        }

        /// <summary>Tanks of that gas the block could have drawn from, and what is in them.</summary>
        public List<MyGasTank> TanksWith(MyCubeBlock from, MyDefinitionId gas, out double available)
        {
            var reachable = new List<MyGasTank>();
            available = 0;
            foreach (var tank in GroupFor(from).Tanks)
            {
                if (tank.BlockDefinition.StoredGasId != gas || !tank.IsWorking) continue;
                var inside = tank.FilledRatio * tank.Capacity;
                if (inside <= 0) continue;
                reachable.Add(tank);
                available += inside;
            }

            return reachable;
        }

        /// <summary>The fuel the block could have pulled, its own inventory first.</summary>
        public List<MyInventory> FuelFor(MyCubeBlock from, MyDefinitionId item, out double amount)
        {
            var reachable = new List<MyInventory>();
            amount = 0;
            var own = from.HasInventory ? from.GetInventory(0) as MyInventory : null;
            if (own != null && own.GetItemAmount(item) > 0)
            {
                reachable.Add(own);
                amount += (double)own.GetItemAmount(item);
            }

            foreach (var inventory in GroupFor(from).Inventories)
            {
                if (inventory == own || inventory.GetItemAmount(item) <= 0) continue;
                reachable.Add(inventory);
                amount += (double)inventory.GetItemAmount(item);
            }

            return reachable;
        }

        /// <summary>The conveyor network this block sits on, worked out once per network.</summary>
        private Group GroupFor(MyCubeBlock block)
        {
            foreach (var known in _groups)
                if (known.Representative == block || Reaches(block, known.Representative))
                    return known;

            var tanks = _tanks.Where(tank => tank == block || Reaches(block, tank)).ToList();
            // An oxygen farm's conveyor endpoint carries no lines at all, so pathfinding takes it
            // nowhere while vanilla still feeds the tanks around it. Such a block is served by
            // everything wired to it. A generator, which does have lines, keeps to its own conveyor
            // network even when there is no tank on it - filling a tank it could not reach would be
            // a gift.
            if (LineCount(block) == 0) tanks = _tanks.ToList();

            var group = new Group
            {
                Representative = block,
                Tanks = tanks,
                Inventories = _inventories.Where(inventory => inventory.Owner is MyCubeBlock owner && Reaches(block, owner)).ToList(),
            };
            _groups.Add(group);
            return group;
        }
    }

    private static readonly MyDefinitionId IceId = new MyDefinitionId(typeof(MyObjectBuilder_Ore), "Ice");

    private static void CompensateGenerator(MyGasGenerator generator, double seconds, Stamp stamp, Network network)
    {
        // A generator that was switched off or unpowered when it froze made nothing while frozen.
        if (!generator.IsWorking || !generator.Enabled) return;
        if (!(generator.BlockDefinition is MyOxygenGeneratorDefinition definition)) return;
        var multiplier = ((Sandbox.ModAPI.IMyGasGenerator)generator).ProductionCapacityMultiplier;

        foreach (var gas in definition.ProducedGases)
        {
            if (gas.Id == MyOxygenGeneratorDefinition.OxygenGasId && !MySession.Static.Settings.EnableOxygen) continue;
            if (gas.IceToGasRatio <= 0) continue;

            var tanks = network.TanksFor(generator, gas.Id, out var room);
            if (room <= 0) continue;

            var ice = network.FuelFor(generator, IceId, out var iceAvailable);
            if (iceAvailable <= 0) return; // no ice for this gas means none for the next one either

            var rated = definition.IceConsumptionPerSecond * gas.IceToGasRatio * multiplier;
            var wanted = Rules.Wanted(rated, Delivered(stamp, gas.Id), seconds);
            var produced = Rules.Producible(wanted, room, iceAvailable, gas.IceToGasRatio);
            if (produced <= 0) continue;

            var burned = TakeFuel(ice, IceId, produced / gas.IceToGasRatio);
            var made = Math.Min(produced, burned * gas.IceToGasRatio);
            Fill(tanks, made);
            _gasCompensated += (long)made;
        }
    }

    private static void CompensateFarm(MyOxygenFarm farm, double seconds, Stamp stamp, Network network)
    {
        if (!farm.IsWorking || !farm.Enabled) return;
        if (!(farm.BlockDefinition is MyOxygenFarmDefinition definition)) return;
        if (definition.ProducedGas == MyOxygenGeneratorDefinition.OxygenGasId && !MySession.Static.Settings.EnableOxygen) return;

        var tanks = network.TanksFor(farm, definition.ProducedGas, out var room);
        if (room <= 0) return;

        var sun = Rules.SunFactor(seconds, stamp.Sun, SunFactor(farm), definition.IsTwoSided);
        if (sun <= 0) return;

        var made = Math.Min(Rules.Wanted(definition.MaxGasOutput * sun, Delivered(stamp, definition.ProducedGas), seconds), room);
        if (made <= 0) return;
        Fill(tanks, made);
        _gasCompensated += (long)made;
    }

    /// <summary>
    /// The uranium a reactor would have burned. It made no power while frozen, but the batteries on
    /// its power network charged as if it had, so the fuel goes with the charge.
    /// </summary>
    private static void CompensateReactor(MyReactor reactor, double seconds, Stamp stamp, Network network)
    {
        if (!(reactor.BlockDefinition is MyReactorDefinition definition)) return;
        if (definition.FuelInfos == null) return;
        var share = Rules.OutputShare(stamp.Power, definition.MaxPowerOutput);
        if (share <= 0) return;

        var paid = Rules.SecondsFor(network.TakeEnergy(Rules.EnergyMwh(stamp.Power, seconds)), stamp.Power);
        if (paid <= 0) return;

        foreach (var fuel in definition.FuelInfos)
        {
            var wanted = Rules.FuelBurned(share, fuel.ConsumptionPerSecond_Items, paid);
            if (wanted <= 0) continue;
            var inventories = network.FuelFor(reactor, fuel.FuelId, out var available);
            if (available <= 0) continue;
            _fuelBurned += TakeFuel(inventories, fuel.FuelId, Math.Min(wanted, available));
        }
    }

    /// <summary>The same for a hydrogen engine, whose fuel is a gas: its own buffer first, then the tanks.</summary>
    private static void CompensateEngine(MyGasFueledPowerProducer engine, double seconds, Stamp stamp, Network network)
    {
        if (!(engine.BlockDefinition is MyGasFueledPowerProducerDefinition definition)) return;
        if (definition.FuelProductionToCapacityMultiplier <= 0) return;
        var paid = Rules.SecondsFor(network.TakeEnergy(Rules.EnergyMwh(stamp.Power, seconds)), stamp.Power);
        var wanted = Rules.GasBurned(stamp.Power, definition.FuelProductionToCapacityMultiplier, paid);
        if (wanted <= 0) return;

        // What the engine is already holding costs nothing to reach.
        var fromBuffer = Math.Min(wanted, engine.Capacity);
        if (fromBuffer > 0)
        {
            engine.Capacity -= (float)fromBuffer;
            _fuelBurned += fromBuffer;
        }

        var left = wanted - fromBuffer;
        if (left <= 0) return;
        var tanks = network.TanksWith(engine, definition.Fuel.FuelId, out var available);
        if (available <= 0) return;
        _fuelBurned += TakeGas(tanks, Math.Min(left, available));
    }

    /// <summary>Spreads the gas over the tanks, each taking what it has room for.</summary>
    private static void Fill(List<MyGasTank> tanks, double amount)
    {
        foreach (var tank in tanks)
        {
            if (amount <= 0) break;
            var free = (1 - tank.FilledRatio) * tank.Capacity;
            if (free <= 0) continue;
            var put = Math.Min(free, amount);
            amount -= put;
            ((Sandbox.ModAPI.IMyGasTank)tank).ChangeFilledRatio(
                Math.Min(1, tank.FilledRatio + put / tank.Capacity), true);
        }
    }

    /// <summary>Burns the fuel, the block's own inventory first. Returns what was really there.</summary>
    private static double TakeFuel(List<MyInventory> inventories, MyDefinitionId item, double amount)
    {
        double taken = 0;
        foreach (var inventory in inventories)
        {
            if (amount - taken <= 0) break;
            var here = (double)inventory.GetItemAmount(item);
            if (here <= 0) continue;
            var take = Math.Min(here, amount - taken);
            inventory.RemoveItemsOfType((MyFixedPoint)take, item);
            taken += take;
        }

        return taken;
    }

    /// <summary>Drains the gas out of the tanks. Returns what was really in them.</summary>
    private static double TakeGas(List<MyGasTank> tanks, double amount)
    {
        double taken = 0;
        foreach (var tank in tanks)
        {
            if (amount - taken <= 0) break;
            var inside = tank.FilledRatio * tank.Capacity;
            if (inside <= 0) continue;
            var take = Math.Min(inside, amount - taken);
            ((Sandbox.ModAPI.IMyGasTank)tank).ChangeFilledRatio(
                Math.Max(0, tank.FilledRatio - take / tank.Capacity), true);
            taken += take;
        }

        return taken;
    }

    // ------------------------------------------------------------------ odds and ends

    /// <summary>What a source was delivering, per gas, at the moment it stopped.</summary>
    private static Dictionary<MyDefinitionId, float> Output(MyResourceSourceComponent source)
    {
        var output = new Dictionary<MyDefinitionId, float>();
        if (source == null) return output;
        foreach (var resource in source.ResourceTypes)
            output[resource] = source.CurrentOutputByType(resource);
        return output;
    }

    private static double Delivered(Stamp stamp, MyDefinitionId gas) =>
        stamp.Output != null && stamp.Output.TryGetValue(gas, out var rate) ? rate : 0;

    /// <summary>True while the block's grid is frozen: the stamp is the mark.</summary>
    public static bool IsFrozen(long blockId) => Stamps.ContainsKey(blockId);

    private static MyFarmPlotLogic FarmPlot(MyCubeBlock block)
    {
        block.Components.TryGet<MyFarmPlotLogic>(out var plot);
        return plot;
    }

    private static float SunFactor(MyOxygenFarm farm)
    {
        var solar = farm.SolarComponent;
        return solar == null || !farm.IsWorking ? 0 : solar.MaxOutput;
    }

    /// <summary>How many conveyor lines the block is on; none means it is not on the network.</summary>
    private static int LineCount(MyCubeBlock block) =>
        block is IMyConveyorEndpointBlock endpoint && endpoint.ConveyorEndpoint != null
            ? endpoint.ConveyorEndpoint.GetLineCount()
            : 0;

    private static bool Reaches(MyCubeBlock from, MyCubeBlock to) =>
        from is IMyConveyorEndpointBlock a && to is IMyConveyorEndpointBlock b &&
        MyGridConveyorSystem.Reachable(a.ConveyorEndpoint, b.ConveyorEndpoint);

    private static ulong FrozenFrames(long blockId, ulong since, ulong now)
    {
        if (since > now)
            SentisOptimisationsPlugin.Log.Warn(
                $"Frozen production: stale future stamp for block {blockId} (stamped {since}, now {now}) - skipped");
        return Rules.FrozenFrames(since, now);
    }

    private static void QueuePlot(MyCubeBlock block, MyFarmPlotLogic plot, int ticks)
    {
        PlotTicksPending.TryRemove(block.EntityId, out var kept);
        var total = ticks + kept;
        if (total <= 0) return;
        if (!plot.IsPlantPlanted || !plot.IsAlive) return; // nothing grows in an empty or dead plot
        PlotQueue.Enqueue(new PlotWork { Block = block, Logic = plot, TicksLeft = total });
    }

    private static void Keep(long blockId, int ticks) =>
        PlotTicksPending.AddOrUpdate(blockId, ticks, (_, kept) => kept + ticks);

    /// <summary>The block left the world: EntityIds are reused, so its stamp must go with it.</summary>
    public static void Forget(long blockId)
    {
        Stamps.TryRemove(blockId, out _);
        PlotTicksPending.TryRemove(blockId, out _);
    }

    public static void ClearAll()
    {
        Stamps.Clear();
        PlotTicksPending.Clear();
        while (PlotQueue.TryDequeue(out _)) { }
        _gasCompensated = 0;
        _plotTicks = 0;
        _fuelBurned = 0;
    }
}
