using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI.Ingame;
using VRage.ObjectBuilders;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Replaces MyRefinery.GetOreFromConveyorSystem: how much ore a refinery takes, when, and how
    /// often it scans the conveyor network.
    ///
    /// Fair share instead of the 30%/60% hysteresis. Vanilla pulls 2000 kg per tick until the input is
    /// 60% full, so the refineries that reach an unload first fill up while others get nothing; a
    /// refinery refines at a fixed speed, so the hoard is processed slowly while its neighbours idle.
    /// Here a refinery tops up to its share: all ore of the network (sources plus the inputs of every
    /// enabled, working refinery on it) divided by the number of those refineries
    /// (<see cref="OreFairShare"/>). Top-ups under <see cref="MinTopUpKg"/> are skipped so a refinery
    /// does not touch the network every tick.
    ///
    /// Per-frame pull budget. Refineries started together run the same cycle and refill in the same
    /// frames; at most <see cref="MaxPullsPerFrame"/> pulls run per frame, emptiest refineries first
    /// (<see cref="PullFrameBudget"/>). A refinery that is almost empty always pulls.
    ///
    /// Backoff on an empty network. Scanning a network that holds no ore finds nothing but still walks
    /// every endpoint and item (measured: 96% of pulls, ~3.9 ms per frame with 6464 refineries). After
    /// a few empty pulls - confirmed by a plain scan, because vanilla also returns nothing while
    /// conveyor paths are still being computed - a refinery waits 2, 4, 8... up to 10 s, with
    /// per-refinery jitter. Ore stored into any sending inventory of the logical grid group, or a
    /// rebuilt conveyor graph, bumps the group's generation and cancels the wait; otherwise refineries
    /// asleep on an idle base would miss a fresh unload.
    /// </summary>
    [PatchShim]
    public static class RefineryOrePull
    {
        private const long PullPeriodFrames = 60;
        // Longest wait between scans of an empty network; ore arrival cancels it anyway.
        private const long MaxBackoffFrames = 10 * 60;
        private const int MaxPullsPerFrame = 30;
        private const double MinTopUpKg = 500;
        // Upper bound of one top-up request; the real limits are the share and the input capacity.
        private const double MaxRequestKg = 1000000;
        private const long SnapshotFrames = 60;

        private static readonly ConditionalWeakTable<MyRefinery, PullBackoff> States =
            new ConditionalWeakTable<MyRefinery, PullBackoff>();
        private static readonly PullFrameBudget FrameBudget = new PullFrameBudget();

        private static readonly FieldInfo ConnectionsField =
            typeof(MyGridConveyorSystem).GetField("m_conveyorConnections", BindingFlags.Instance | BindingFlags.NonPublic);
        private static FieldInfo _pullElementsField;

        private static readonly ConditionalWeakTable<object, StrongBox<long>> Generations =
            new ConditionalWeakTable<object, StrongBox<long>>();
        private static long _generationSource;

        private sealed class OreSnapshot
        {
            public bool Counted;
            public long Frame;
            public double SourceOre;
            public double RefineryInputOre;
            public int Refineries;
        }

        private static readonly ConditionalWeakTable<object, OreSnapshot> Snapshots =
            new ConditionalWeakTable<object, OreSnapshot>();

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("RefineryOrePull", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            ctx.GetPattern(typeof(MyRefinery).GetMethod("GetOreFromConveyorSystem", BindingFlags.Instance | BindingFlags.NonPublic))
                .Prefixes.Add(typeof(RefineryOrePull).GetMethod(nameof(GetOreFromConveyorSystemPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic));

            ctx.GetPattern(typeof(MyInventoryBase).GetMethod(nameof(MyInventoryBase.RaiseContentsAdded),
                    BindingFlags.Instance | BindingFlags.Public))
                .Suffixes.Add(typeof(RefineryOrePull).GetMethod(nameof(ContentsAddedSuffix),
                    BindingFlags.Static | BindingFlags.NonPublic));
            // Stacking onto an existing item raises ContentsAdded; a brand new stack (ore into an empty
            // container) only raises InventoryContentChanged. Both paths have to wake refineries.
            ctx.GetPattern(typeof(MyInventoryBase).GetMethod(nameof(MyInventoryBase.RaiseInventoryContentChanged),
                    BindingFlags.Instance | BindingFlags.Public))
                .Suffixes.Add(typeof(RefineryOrePull).GetMethod(nameof(InventoryContentChangedSuffix),
                    BindingFlags.Static | BindingFlags.NonPublic));
            ctx.GetPattern(typeof(MyGridConveyorSystem).GetMethod("OnConveyorEndpointMappingUpdateCompleted",
                    BindingFlags.Instance | BindingFlags.NonPublic))
                .Suffixes.Add(typeof(RefineryOrePull).GetMethod(nameof(MappingUpdatedSuffix),
                    BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool GetOreFromConveyorSystemPrefix(MyRefinery __instance)
        {
            try
            {
                var refinery = __instance;
                if (!refinery.UseConveyorSystem || !refinery.IsWorking || !refinery.Enabled) return false;
                var input = refinery.InputInventory;
                var constraint = input?.Constraint;
                var system = refinery.CubeGrid?.GridSystems?.ConveyorSystem;
                if (constraint == null || system == null) return true;

                var frame = MySession.Static.GameplayFrameCounter;
                var generation = Generation(refinery.CubeGrid);
                var state = States.GetValue(refinery, _ => new PullBackoff());
                if (state.ShouldSkip(frame, MaxBackoffFrames, generation)) return false;

                // Endpoint mapping not computed yet: vanilla behaviour until it is.
                var snapshot = NetworkOre(system, constraint, refinery, frame);
                if (snapshot == null) return true;

                var held = AcceptedAmount(input, constraint);
                var allowed = OreFairShare.Allowed(MaxRequestKg, snapshot.SourceOre, snapshot.RefineryInputOre,
                    snapshot.Refineries, held);
                var starving = held < OreFairShare.MinPullKg;
                if (allowed <= 0 || (!starving && allowed < MinTopUpKg)) return false;

                var fill = input.VolumeFillFactor;
                if (fill >= 0.99f) return false;
                if (!FrameBudget.TryAcquire(frame, MaxPullsPerFrame, starving, fill)) return false;

                var pulled = system.PullItems(constraint, (MyFixedPoint)allowed, refinery, input);
                var networkHasItems = pulled > 0 || NetworkHasAcceptedItems(system, constraint, refinery, input, frame, generation);
                state.OnResult(frame, networkHasItems, PullPeriodFrames, MaxBackoffFrames, generation,
                    Jitter(refinery.EntityId));
                return false;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "RefineryOrePull failed");
                return true;
            }
        }

        private static double Jitter(long entityId)
        {
            var z = (ulong)entityId + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z % 1024) / 1023.0;
        }

        private static object NetworkKey(MyCubeGrid grid)
        {
            if (grid == null) return null;
            return (object)MyCubeGridGroups.Static?.Logical.GetGroup(grid) ?? grid;
        }

        private static long Generation(MyCubeGrid grid)
        {
            var key = NetworkKey(grid);
            if (key == null) return 0;
            return Generations.GetValue(key, _ => new StrongBox<long>(Interlocked.Increment(ref _generationSource))).Value;
        }

        private static void Bump(MyCubeGrid grid)
        {
            var key = NetworkKey(grid);
            if (key == null) return;
            Generations.GetValue(key, _ => new StrongBox<long>()).Value = Interlocked.Increment(ref _generationSource);
        }

        private static void InventoryContentChangedSuffix(MyInventoryBase __instance, MyPhysicalInventoryItem item,
            MyFixedPoint amount)
        {
            if (amount > 0) ContentsAddedSuffix(__instance, item);
        }

        private static void ContentsAddedSuffix(MyInventoryBase __instance, MyPhysicalInventoryItem item)
        {
            if (!(item.Content is MyObjectBuilder_Ore)) return;
            var inventory = __instance as MyInventory;
            if (inventory == null || (inventory.GetFlags() & MyInventoryFlags.CanSend) == 0) return;
            var block = inventory.Owner as MyCubeBlock;
            if (block != null) Bump(block.CubeGrid);
        }

        private static readonly Func<MyGridConveyorSystem, MyCubeGrid> SystemGrid = BuildSystemGridGetter();

        private static Func<MyGridConveyorSystem, MyCubeGrid> BuildSystemGridGetter()
        {
            var getter = typeof(MyGridConveyorSystem).BaseType?
                .GetProperty("Grid", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetGetMethod(true);
            if (getter == null) return _ => null;
            return system => getter.Invoke(system, null) as MyCubeGrid;
        }

        private static void MappingUpdatedSuffix(MyGridConveyorSystem __instance)
        {
            Bump(SystemGrid(__instance));
        }

        /// <summary>
        /// Ore the refinery's network can offer (sending inventories) and ore already sitting in the
        /// inputs of the enabled, working refineries it shares that network with; the caller is always
        /// one of them. Recounted at most once per second per logical grid group; null when the
        /// endpoint mapping is not known yet.
        /// </summary>
        private static OreSnapshot NetworkOre(MyGridConveyorSystem system, MyInventoryConstraint constraint,
            MyRefinery self, long frame)
        {
            var key = NetworkKey(self.CubeGrid);
            if (key == null) return null;
            var snapshot = Snapshots.GetValue(key, _ => new OreSnapshot());
            if (snapshot.Counted && frame - snapshot.Frame < SnapshotFrames) return snapshot;
            var elements = PullElements(system, self);
            if (elements == null) return null;

            double sourceOre = 0, inputOre = AcceptedAmount(self.InputInventory, constraint);
            var refineries = 1;
            foreach (var element in elements)
            {
                var block = element?.ConveyorEndpoint?.CubeBlock;
                if (block == null || block == self) continue;
                var otherRefinery = block as MyRefinery;
                if (otherRefinery != null && otherRefinery.Enabled && otherRefinery.IsWorking &&
                    otherRefinery.UseConveyorSystem)
                {
                    refineries++;
                    inputOre += AcceptedAmount(otherRefinery.InputInventory, constraint);
                }
                for (var i = 0; i < block.InventoryCount; i++)
                {
                    var inventory = block.GetInventory(i);
                    if (inventory == null || (inventory.GetFlags() & MyInventoryFlags.CanSend) == 0) continue;
                    sourceOre += AcceptedAmount(inventory, constraint);
                }
            }
            snapshot.Counted = true;
            snapshot.Frame = frame;
            snapshot.SourceOre = sourceOre;
            snapshot.RefineryInputOre = inputOre;
            snapshot.Refineries = refineries;
            return snapshot;
        }

        private static double AcceptedAmount(MyInventory inventory, MyInventoryConstraint constraint)
        {
            if (inventory == null || constraint == null) return 0;
            double total = 0;
            foreach (var item in inventory.GetItems())
                if (constraint.Check(item.Content.GetId())) total += (double)item.Amount;
            return total;
        }

        private static List<IMyConveyorEndpointBlock> PullElements(MyGridConveyorSystem system, IMyConveyorEndpointBlock start)
        {
            if (ConnectionsField == null) return null;
            var connections = ConnectionsField.GetValue(system) as IDictionary;
            if (connections == null || !connections.Contains(start)) return null;
            var mapping = connections[start];
            if (mapping == null) return null;
            if (_pullElementsField == null)
                _pullElementsField = mapping.GetType().GetField("pullElements", BindingFlags.Instance | BindingFlags.Public);
            return _pullElementsField?.GetValue(mapping) as List<IMyConveyorEndpointBlock>;
        }

        private sealed class AcceptedItemsScan
        {
            public long Generation = -1;
            public long Frame = -1;
            public bool HasItems;
        }

        // Last scan result per logical grid group and constraint (one per refinery type).
        private static readonly ConditionalWeakTable<object, Dictionary<MyInventoryConstraint, AcceptedItemsScan>> Scans =
            new ConditionalWeakTable<object, Dictionary<MyInventoryConstraint, AcceptedItemsScan>>();

        /// <summary>
        /// Whether the network still holds ore for this refinery after a pull that got nothing,
        /// remembered per network. The answer is asked after every empty pull, and on a base whose ore
        /// has run out that is every refinery every few seconds, each walking every block, inventory and
        /// item of the network to find the same nothing (measured: 1.3 s of every 55, plus the inventory
        /// lookups under it). "Nothing" stays true until the network's generation changes, which
        /// already happens on any ore stored into a sending inventory and on a rebuilt conveyor graph -
        /// the same signal that wakes refineries from their backoff. "Something" is only reused within
        /// the frame, because pulls take it away.
        /// </summary>
        private static bool NetworkHasAcceptedItems(MyGridConveyorSystem system, MyInventoryConstraint constraint,
            MyRefinery start, MyInventory destination, long frame, long generation)
        {
            var key = NetworkKey(start.CubeGrid);
            if (key == null) return NetworkHasAcceptedItems(system, constraint, start, destination);
            var byConstraint = Scans.GetValue(key, _ => new Dictionary<MyInventoryConstraint, AcceptedItemsScan>());
            if (!byConstraint.TryGetValue(constraint, out var scan))
                byConstraint[constraint] = scan = new AcceptedItemsScan();
            if (scan.Generation == generation && (!scan.HasItems || scan.Frame == frame)) return scan.HasItems;
            scan.HasItems = NetworkHasAcceptedItems(system, constraint, start, destination);
            scan.Generation = generation;
            scan.Frame = frame;
            return scan.HasItems;
        }

        /// <summary>
        /// True when any sending inventory reachable from <paramref name="start"/> holds an item the
        /// constraint accepts, or when that cannot be determined. Mirrors the candidate filter of
        /// vanilla PullItems without its transfer and path checks.
        /// </summary>
        private static bool NetworkHasAcceptedItems(MyGridConveyorSystem system, MyInventoryConstraint constraint,
            IMyConveyorEndpointBlock start, MyInventory destination)
        {
            var elements = PullElements(system, start);
            if (elements == null) return true;
            foreach (var element in elements)
            {
                var block = element?.ConveyorEndpoint?.CubeBlock;
                if (block == null) continue;
                for (var i = 0; i < block.InventoryCount; i++)
                {
                    var inventory = block.GetInventory(i);
                    if (inventory == null || inventory == destination ||
                        (inventory.GetFlags() & MyInventoryFlags.CanSend) == 0) continue;
                    foreach (var item in inventory.GetItems())
                        if (constraint.Check(item.Content.GetId())) return true;
                }
            }
            return false;
        }
    }
}
