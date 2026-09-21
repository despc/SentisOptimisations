using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Components;
using VRage.Game.ObjectBuilders.ComponentSystem;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Network;
using VRage.ObjectBuilders;
using VRage;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// Keeps the object builder of a grid that is streamed to clients and updates only what changed.
    ///
    /// Streaming a grid to a client builds its whole object builder on the game thread (~3 ms and
    /// ~450 KB for a 400 block grid, much more for a big one), and vanilla does that again for every
    /// client. Almost nothing in it is client specific: only the scripts of programmable blocks,
    /// which depend on the client's rights (see <see cref="FuckScriptThief"/>), so three variants are
    /// enough - everything, scripts shared with the faction, and only scripts shared with everybody.
    ///
    /// The builder is kept between frames and refreshed in place:
    /// - grid fields that the grid changes by itself (position, velocities, presence tiers, name,
    ///   flags, its own components) are copied over on every use;
    /// - a block whose state changed (integrity, ownership, inventory, a terminal property) has only
    ///   its own block builder rebuilt;
    /// - adding, removing or closing a block, and anything else that can change the block list, drops
    ///   the builder and it is built from scratch.
    ///
    /// <see cref="VerifyCachedBuilders"/> compares the refreshed builder with a freshly built one and
    /// counts the differences; the replication test turns it on for one window.
    /// </summary>
    [PatchShim]
    public static class GridStreamBuilders
    {
        public enum Access
        {
            /// <summary>Admin, the owner, or a grid without an owner: nothing is hidden.</summary>
            Full,
            /// <summary>Same faction as the owner: scripts shared with the faction stay.</summary>
            Faction,
            /// <summary>Everyone else: only scripts shared with everybody stay.</summary>
            None,
        }

        public const string HiddenScript = "You don't need to see this";
        /// <summary>Grids kept at once; the least recently streamed one is dropped first.</summary>
        public const int MaxEntries = 256;
        /// <summary>With more changed blocks than this, building the whole grid again is cheaper.</summary>
        public const int MaxDirtyBlocks = 48;

        private sealed class Entry
        {
            public MyCubeGrid Grid;
            public MyObjectBuilder_CubeGrid Full;
            public MyObjectBuilder_CubeGrid Faction;
            public MyObjectBuilder_CubeGrid None;
            public readonly Dictionary<MySlimBlock, int> BlockIndex = new Dictionary<MySlimBlock, int>();
            public readonly HashSet<MySlimBlock> DirtyBlocks = new HashSet<MySlimBlock>();
            /// <summary>Timers tick every frame with no change event, so they are copied on every use.</summary>
            public readonly List<KeyValuePair<MyTimerComponent, MyObjectBuilder_TimerComponent>> Timers =
                new List<KeyValuePair<MyTimerComponent, MyObjectBuilder_TimerComponent>>();
            /// <summary>Blocks whose builder changes on its own (stored power, gas, piston position...).</summary>
            public readonly List<MySlimBlock> VolatileBlocks = new List<MySlimBlock>();
            public bool StructureStale;
            public long LastUsedFrame;
            public Action<MySlimBlock> BlockChanged;
            public Action<MySlimBlock> BlockDirty;
            public Action<MyCubeGrid> GridChanged;
            public Action<MyEntity> Closing;
        }

        private static readonly Dictionary<long, Entry> Entries = new Dictionary<long, Entry>();
        private static readonly Dictionary<IMyReplicable, long> GridIds = new Dictionary<IMyReplicable, long>();

        public static bool VerifyCachedBuilders;
        public static long Builds;
        public static long Reuses;
        public static long BlockRebuilds;
        public static long StructureRebuilds;
        public static long IndexMismatches;
        /// <summary>Block groups that had no name and were given one so the grid could be sent.</summary>
        public static long NamedEmptyGroups;
        public static long StaleByEvent;
        public static long StaleByDirtyBlocks;
        public static long BuildTicks;
        public static long RefreshTicks;
        public static long VerifyMismatches;
        public static string VerifyFirstDifference;
        /// <summary>Distinct XML elements that differed, so one run shows every field that drifts.</summary>
        public static readonly HashSet<string> VerifyDifferentElements = new HashSet<string>();

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("GridStreamBuilders", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags anyStatic = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            ctx.GetPattern(typeof(MyInventoryBase).GetMethod(nameof(MyInventoryBase.RaiseInventoryContentChanged),
                    BindingFlags.Instance | BindingFlags.Public))
                .Suffixes.Add(typeof(GridStreamBuilders).GetMethod(nameof(InventoryChangedSuffix), anyStatic));
            var propertyGroup = typeof(MyCubeGrid).Assembly
                .GetType("Sandbox.Game.Replication.StateGroups.MyPropertySyncStateGroup", true);
            ctx.GetPattern(propertyGroup.GetMethod("Notify", BindingFlags.Instance | BindingFlags.NonPublic))
                .Suffixes.Add(typeof(GridStreamBuilders).GetMethod(nameof(PropertyChangedSuffix), anyStatic));
        }

        /// <summary>Game thread only: called while the replication server serializes a grid for a client.</summary>
        public static MyObjectBuilder_CubeGrid Get(MyCubeGrid grid, IMyReplicable replicable, Endpoint forClient, Access access, long frame)
        {
            var started = Stopwatch.GetTimestamp();
            GridIds[replicable] = grid.EntityId;
            if (!Entries.TryGetValue(grid.EntityId, out var entry) || entry.Grid != grid)
            {
                if (entry != null)
                {
                    Unsubscribe(entry);
                    Entries.Remove(grid.EntityId);
                }
                entry = new Entry { Grid = grid };
                Subscribe(entry);
                Entries[grid.EntityId] = entry;
                Evict();
            }
            entry.LastUsedFrame = frame;

            if (entry.Full == null || entry.StructureStale || entry.DirtyBlocks.Count > MaxDirtyBlocks)
            {
                if (entry.Full != null)
                {
                    StructureRebuilds++;
                    if (entry.StructureStale) StaleByEvent++;
                    else StaleByDirtyBlocks++;
                }
                Build(entry, replicable, forClient);
                BuildTicks += Stopwatch.GetTimestamp() - started;
            }
            else
            {
                Reuses++;
                Refresh(entry, replicable, forClient);
                RefreshTicks += Stopwatch.GetTimestamp() - started;
                if (VerifyCachedBuilders) Verify(entry, replicable, forClient);
            }

            switch (access)
            {
                case Access.Faction:
                    return entry.Faction ?? (entry.Faction = Mask(entry.Full, keepFactionShared: true));
                case Access.None:
                    return entry.None ?? (entry.None = Mask(entry.Full, keepFactionShared: false));
                default:
                    return entry.Full;
            }
        }

        /// <summary>True when the grid's builder is kept and only needs a cheap refresh.</summary>
        public static bool IsReady(IMyReplicable replicable) =>
            replicable != null && GridIds.TryGetValue(replicable, out var gridId) &&
            Entries.TryGetValue(gridId, out var entry) && entry.Full != null && !entry.StructureStale &&
            entry.DirtyBlocks.Count <= MaxDirtyBlocks;

        /// <summary>Drops everything, e.g. when the world is unloaded.</summary>
        public static void Clear()
        {
            foreach (var entry in Entries.Values) Unsubscribe(entry);
            Entries.Clear();
            GridIds.Clear();
        }

        private static void Build(Entry entry, IMyReplicable replicable, Endpoint forClient)
        {
            Builds++;
            using (MyReplicationLayer.StartSerializingReplicable(replicable, forClient))
                entry.Full = (MyObjectBuilder_CubeGrid)entry.Grid.GetObjectBuilder();
            entry.Faction = null;
            entry.None = null;
            entry.StructureStale = false;
            entry.DirtyBlocks.Clear();
            NameEmptyGroups(entry.Full);
            IndexBlocks(entry);
            IndexTimers(entry);
            IndexVolatileBlocks(entry);
        }

        /// <summary>
        /// A block group without a name cannot be sent to anybody.
        ///
        /// The network serializer refuses a null name outright, and the failure lands on a worker
        /// thread in the middle of a stream: the client is told the grid is coming, the data never
        /// arrives, and the grid sits there half-replicated for as long as the player looks at it.
        /// A blueprint saved with such a group - and they exist, one came out of the operator's own
        /// projector - therefore makes the whole grid carrying it invisible to everyone.
        ///
        /// Saving and loading tolerate the missing name, so nothing else ever complains. Giving the
        /// group a name for the copy that goes over the wire costs nothing and keeps the grid
        /// reachable; groups inside a projector's blueprint are walked too, which is where the one
        /// in the bench lives.
        /// </summary>
        private static void NameEmptyGroups(MyObjectBuilder_CubeGrid builder)
        {
            if (builder == null) return;
            if (builder.BlockGroups != null)
                foreach (var group in builder.BlockGroups)
                    if (group != null && string.IsNullOrEmpty(group.Name))
                    {
                        group.Name = UnnamedGroup;
                        NamedEmptyGroups++;
                    }

            if (builder.CubeBlocks == null) return;
            foreach (var block in builder.CubeBlocks) NameEmptyGroups(block);
        }

        /// <summary>The same for the blueprints a projector carries inside its own builder.</summary>
        private static void NameEmptyGroups(MyObjectBuilder_CubeBlock block)
        {
            var projector = block as MyObjectBuilder_ProjectorBase;
            if (projector?.ProjectedGrids == null) return;
            foreach (var projected in projector.ProjectedGrids) NameEmptyGroups(projected);
        }

        public const string UnnamedGroup = "Group";

        /// <summary>Pairs every block timer with its place in the builder.</summary>
        private static void IndexTimers(Entry entry)
        {
            entry.Timers.Clear();
            foreach (var pair in entry.BlockIndex)
            {
                var timer = TimerOf(pair.Key);
                if (timer == null) continue;
                var timerBuilder = TimerBuilderOf(entry.Full.CubeBlocks[pair.Value]);
                if (timerBuilder != null) entry.Timers.Add(new KeyValuePair<MyTimerComponent, MyObjectBuilder_TimerComponent>(timer, timerBuilder));
            }
        }

        /// <summary>
        /// Blocks that keep changing without any event: what they store (power, gas, charge) or where
        /// they are (piston, rotor, door) is read live when the builder is made.
        /// </summary>
        private static void IndexVolatileBlocks(Entry entry)
        {
            entry.VolatileBlocks.Clear();
            foreach (var pair in entry.BlockIndex)
                if (IsVolatile(pair.Key)) entry.VolatileBlocks.Add(pair.Key);
        }

        public static bool IsVolatile(MySlimBlock block)
        {
            var fat = block.FatBlock;
            if (fat == null) return false;
            return fat is Sandbox.ModAPI.IMyBatteryBlock || fat is Sandbox.ModAPI.IMyGasTank ||
                   fat is Sandbox.ModAPI.IMyJumpDrive || fat is Sandbox.ModAPI.IMyPistonBase ||
                   fat is Sandbox.ModAPI.IMyMotorBase || fat is Sandbox.ModAPI.IMyDoor ||
                   fat is SpaceEngineers.Game.ModAPI.IMyTimerBlock;
        }

        private static MyTimerComponent TimerOf(MySlimBlock block) =>
            block.FatBlock != null && block.FatBlock.Components.TryGet<MyTimerComponent>(out var timer) ? timer : null;

        private static MyObjectBuilder_TimerComponent TimerBuilderOf(MyObjectBuilder_CubeBlock block)
        {
            var container = block?.ComponentContainer;
            if (container?.Components == null) return null;
            foreach (var data in container.Components)
                if (data.Component is MyObjectBuilder_TimerComponent timer) return timer;
            return null;
        }

        /// <summary>Maps every block to its place in the builder, so a single block can be replaced later.</summary>
        private static void IndexBlocks(Entry entry)
        {
            entry.BlockIndex.Clear();
            var builders = entry.Full.CubeBlocks;
            var index = 0;
            foreach (var block in entry.Grid.GetBlocks())
            {
                // Vanilla builds the list in this same order, skipping blocks without a builder.
                if (index >= builders.Count || (Vector3I)builders[index].Min != block.Min)
                {
                    entry.BlockIndex.Clear();
                    entry.StructureStale = true;
                    IndexMismatches++;
                    return;
                }
                entry.BlockIndex[block] = index++;
            }
            if (index != builders.Count)
            {
                entry.BlockIndex.Clear();
                entry.StructureStale = true;
                IndexMismatches++;
            }
        }

        /// <summary>Copies over what the grid changes by itself and rebuilds the blocks that changed.</summary>
        private static void Refresh(Entry entry, IMyReplicable replicable, Endpoint forClient)
        {
            var grid = entry.Grid;
            var builder = entry.Full;
            builder.PositionAndOrientation = new MyPositionAndOrientation
            {
                Position = grid.PositionComp.GetPosition(),
                Up = (Vector3)grid.WorldMatrix.Up,
                Forward = (Vector3)grid.WorldMatrix.Forward,
            };
            builder.PersistentFlags = grid.Render.PersistentFlags;
            builder.Name = grid.Name;
            builder.DisplayName = grid.DisplayName;
            builder.IsStatic = grid.IsStatic;
            builder.GridPresenceTier = grid.GridPresenceTier;
            builder.PlayerPresenceTier = grid.PlayerPresenceTier;
            builder.playedTime = grid.m_playedTime;
            if (grid.Physics != null)
            {
                builder.LinearVelocity = grid.Physics.LinearVelocity;
                builder.AngularVelocity = grid.Physics.AngularVelocity;
            }

            var rebuiltBlocks = false;
            if (entry.DirtyBlocks.Count > 0 || entry.VolatileBlocks.Count > 0)
            {
                using (MyReplicationLayer.StartSerializingReplicable(replicable, forClient))
                {
                    foreach (var block in entry.DirtyBlocks) rebuiltBlocks |= RebuildBlock(entry, block);
                    foreach (var block in entry.VolatileBlocks) rebuiltBlocks |= RebuildBlock(entry, block);
                }
                entry.DirtyBlocks.Clear();
                if (rebuiltBlocks)
                {
                    // The masked copies still hold the block builders that were just replaced, and a
                    // rebuilt block carries a new timer builder.
                    entry.Faction = null;
                    entry.None = null;
                    IndexTimers(entry);
                }
            }
            foreach (var pair in entry.Timers)
            {
                var timer = pair.Key;
                var timerBuilder = pair.Value;
                timerBuilder.FramesFromLastTrigger = timer.FramesFromLastTrigger;
                timerBuilder.TimerTickInFrames = timer.TimerTickInFrames;
                timerBuilder.TimerEnabled = timer.TimerEnabled;
                timerBuilder.Repeat = timer.Repeat;
                timerBuilder.RemoveEntityOnTimer = timer.RemoveEntityOnTimer;
                timerBuilder.IsSessionUpdateEnabled = timer.IsSessionUpdateEnabled;
            }
            // The grid's own components (not the blocks') are small and have no change signal.
            builder.ComponentContainer = grid.Components.Serialize();
        }

        private static bool RebuildBlock(Entry entry, MySlimBlock block)
        {
            if (!entry.BlockIndex.TryGetValue(block, out var index)) { entry.StructureStale = true; return false; }
            var blockBuilder = block.GetObjectBuilder();
            if (blockBuilder == null) { entry.StructureStale = true; return false; }
            NameEmptyGroups(blockBuilder);
            entry.Full.CubeBlocks[index] = blockBuilder;
            BlockRebuilds++;
            return true;
        }

        private static void Verify(Entry entry, IMyReplicable replicable, Endpoint forClient)
        {
            MyObjectBuilder_CubeGrid fresh;
            using (MyReplicationLayer.StartSerializingReplicable(replicable, forClient))
                fresh = (MyObjectBuilder_CubeGrid)entry.Grid.GetObjectBuilder();
            var expected = Xml(fresh);
            var actual = Xml(entry.Full);
            if (expected == actual) return;
            VerifyMismatches++;
            var a = expected.Split((char)10);
            var b = actual.Split((char)10);
            var found = false;
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                if (a[i] == b[i]) continue;
                if (VerifyDifferentElements.Count < 40) VerifyDifferentElements.Add(ElementOf(a[i]));
                if (!found && VerifyFirstDifference == null)
                    VerifyFirstDifference = entry.Grid.DisplayName + " line " + i + ": fresh '" + a[i].Trim() +
                                            "' cached '" + b[i].Trim() + "'";
                found = true;
            }
            if (found) return;
            if (VerifyDifferentElements.Count < 40) VerifyDifferentElements.Add("(length)");
            if (VerifyFirstDifference == null)
                VerifyFirstDifference = entry.Grid.DisplayName + ": length " + a.Length + " vs " + b.Length;
        }

        private static string ElementOf(string line)
        {
            var open = line.IndexOf('<');
            if (open < 0) return line.Trim();
            var end = line.IndexOfAny(new[] { '>', ' ' }, open + 1);
            return end < 0 ? line.Trim() : line.Substring(open + 1, end - open - 1);
        }

        private static string Xml(MyObjectBuilder_Base builder)
        {
            using (var stream = new System.IO.MemoryStream())
            {
                VRage.ObjectBuilders.Private.MyObjectBuilderSerializerKeen.SerializeXML(stream, builder);
                return System.Text.Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static void Subscribe(Entry entry)
        {
            entry.BlockChanged = _ => entry.StructureStale = true;
            entry.BlockDirty = block => entry.DirtyBlocks.Add(block);
            entry.GridChanged = _ => entry.StructureStale = true;
            entry.Closing = _ => entry.StructureStale = true;
            var grid = entry.Grid;
            grid.OnBlockAdded += entry.BlockChanged;
            grid.OnBlockRemoved += entry.BlockChanged;
            grid.OnBlockClosed += entry.BlockChanged;
            grid.OnBlockIntegrityChanged += entry.BlockDirty;
            grid.OnBlockOwnershipChanged += entry.GridChanged;
            grid.OnGridChanged += entry.GridChanged;
            grid.OnHierarchyUpdated += entry.GridChanged;
            grid.OnMarkForClose += entry.Closing;
        }

        private static void Unsubscribe(Entry entry)
        {
            var grid = entry.Grid;
            if (grid == null) return;
            grid.OnBlockAdded -= entry.BlockChanged;
            grid.OnBlockRemoved -= entry.BlockChanged;
            grid.OnBlockClosed -= entry.BlockChanged;
            grid.OnBlockIntegrityChanged -= entry.BlockDirty;
            grid.OnBlockOwnershipChanged -= entry.GridChanged;
            grid.OnGridChanged -= entry.GridChanged;
            grid.OnHierarchyUpdated -= entry.GridChanged;
            grid.OnMarkForClose -= entry.Closing;
        }

        private static void Evict()
        {
            if (Entries.Count <= MaxEntries) return;
            long oldestId = 0;
            var oldestFrame = long.MaxValue;
            foreach (var pair in Entries)
            {
                if (pair.Value.LastUsedFrame >= oldestFrame) continue;
                oldestFrame = pair.Value.LastUsedFrame;
                oldestId = pair.Key;
            }
            if (!Entries.TryGetValue(oldestId, out var entry)) return;
            Unsubscribe(entry);
            Entries.Remove(oldestId);
        }

        private static void InventoryChangedSuffix(MyInventoryBase __instance)
        {
            if (Entries.Count == 0) return;
            MarkDirty((__instance.Entity as MyCubeBlock)?.SlimBlock);
        }

        private static void PropertyChangedSuffix(object __instance)
        {
            if (Entries.Count == 0) return;
            if (!((__instance as IMyStateGroup)?.Owner is IMyEntityReplicable owner)) return;
            if (!MyEntities.TryGetEntityById(owner.EntityId, out var entity)) return;
            MarkDirty((entity as MyCubeBlock)?.SlimBlock);
        }

        private static void MarkDirty(MySlimBlock block)
        {
            var grid = block?.CubeGrid;
            if (grid == null) return;
            if (Entries.TryGetValue(grid.EntityId, out var entry)) entry.DirtyBlocks.Add(block);
        }

        /// <summary>
        /// A copy of the builder where programmable blocks the client may not read carry no script.
        /// Everything else, including every other block builder, is shared with the full builder.
        /// </summary>
        public static MyObjectBuilder_CubeGrid Mask(MyObjectBuilder_CubeGrid full, bool keepFactionShared)
        {
            var masked = (MyObjectBuilder_CubeGrid)MemberwiseCloneMethod.Invoke(full, null);
            masked.CubeBlocks = new List<MyObjectBuilder_CubeBlock>(full.CubeBlocks.Count);
            foreach (var block in full.CubeBlocks)
            {
                if (!(block is MyObjectBuilder_MyProgrammableBlock program) || IsScriptVisible(program, keepFactionShared))
                {
                    masked.CubeBlocks.Add(block);
                    continue;
                }
                var hidden = (MyObjectBuilder_MyProgrammableBlock)MemberwiseCloneMethod.Invoke(program, null);
                hidden.Program = HiddenScript;
                masked.CubeBlocks.Add(hidden);
            }
            return masked;
        }

        public static bool IsScriptVisible(MyObjectBuilder_CubeBlock block, bool keepFactionShared)
        {
            if (block.ShareMode == MyOwnershipShareModeEnum.All) return true;
            return keepFactionShared && block.ShareMode == MyOwnershipShareModeEnum.Faction;
        }

        private static readonly MethodInfo MemberwiseCloneMethod =
            typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
    }
}
