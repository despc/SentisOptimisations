using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Torch.Managers.PatchManager;
using VRage.Collections;
using VRage.Game.ModAPI;
using VRageMath;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Airtightness of a large grid being welded, recomputed less often and faster.
    ///
    /// From the server (14.10.2026, players welding large ships): a worker spent 44 % of its time in
    /// <c>MyGridGasSystem.RefreshRoomData</c>, nearly all of it in <c>RefreshTopRoom</c>. A grid with an air vent
    /// recomputes its rooms on a worker after blocks are added, and any block added next to the outside air marks the
    /// outside "room" dirty; <c>RefreshTopRoom</c> then floods the whole outside of the grid's bounding box anew - every
    /// cell of the box not inside the hull, with two hash sets per cell and two block lookups per side. On a large
    /// ship that is about a second, and welders keep adding blocks: as soon as one refresh is over the next one starts.
    ///
    /// Three things here, none of which changes what the rooms come out as:
    ///  * a block placed unfinished (a welder puts a projected block at its first component, so does a player) is
    ///    not airtight on any side and is not a door: the rooms stay what they were, and no refresh is asked for. The
    ///    block asks again when it is welded up to its last build stage, as in the game; one that grows the bounding
    ///    box, a door and a gas block go to the game as they are;
    ///  * the outside is flooded with a bit per cell of the box instead of hash sets, a block looked up only in a cell
    ///    that has one (read off the grid's cells first), the room written straight into the cube's chunk arrays, the
    ///    same airtightness checks as the game's; the cells that were outside and are no more are read off the cube in
    ///    one pass, and the empty rooms (<c>GenerateEmptyRooms</c>, every cell of the box) pass over the cells in a
    ///    room without the cube's lookups. In the scenario gas_weld_refresh (a frame of 100 x 50 x 200, a million
    ///    cells): 833 ms a refresh in the game, 135 ms here (Release build);
    ///  * a grid's next refresh waits for <see cref="PauseFactor"/> times as long as its last refresh took (at most
    ///    <see cref="MaxPauseMs"/>): the blocks welded meanwhile go in together, and a worker spends at most a fifth of
    ///    its time on one grid. A grid whose refresh takes a millisecond waits four milliseconds; blocks removed (a
    ///    breach) are not held back.
    /// </summary>
    [PatchShim]
    public static class GasRoomRefresh
    {
        public const int PauseFactor = 4;
        public const int MaxPauseMs = 5000;

        /// <summary>A bounding box larger than this is left to the game (a bit per cell: 64 MB).</summary>
        private const long MaxCells = 1L << 29;

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Func<MyGridGasSystem, object> Context = Getter<object>("m_context");
        private static readonly Func<MyGridGasSystem, MyConcurrentList<MyOxygenRoom>> Rooms = Getter<MyConcurrentList<MyOxygenRoom>>("m_rooms");
        private static readonly Func<MyGridGasSystem, MyOxygenCube> CubeRoom = Getter<MyOxygenCube>("m_cubeRoom");
        private static readonly Func<MyGridGasSystem, HashSet<Vector3I>> RoomCellsBuffer = Getter<HashSet<Vector3I>>("m_roomCellsBuffer");
        private static readonly Func<MyGridGasSystem, Vector3I> StoredMin = Getter<Vector3I>("m_storedGridMin");
        private static readonly Func<MyGridGasSystem, Vector3I> StoredMax = Getter<Vector3I>("m_storedGridMax");
        private static readonly Func<MyGridGasSystem, Vector3I> PreviousMin = Getter<Vector3I>("m_previousGridMin");
        private static readonly Func<MyGridGasSystem, Vector3I> PreviousMax = Getter<Vector3I>("m_previousGridMax");
        private static readonly Func<MyGridGasSystem, bool> IsProcessing = Getter<bool>("m_isProcessingData");
        private static readonly Func<MyGridGasSystem, MyCubeGrid> Grid = GridGetter();
        private static readonly Func<MyGridGasSystem, HashSet<Vector3I>, float, Vector3I, MyOxygenRoom> CreateAirtightRoom = Method<Func<MyGridGasSystem, HashSet<Vector3I>, float, Vector3I, MyOxygenRoom>>("CreateAirtightRoom");
        private static readonly Func<MyCubeGrid, ConcurrentDictionary<Vector3I, MyCube>> GridCubes = FieldGetter<MyCubeGrid, ConcurrentDictionary<Vector3I, MyCube>>("m_cubes");
        private static readonly Func<MyOxygenCube, ConcurrentDictionary<Vector3I, int[]>> CubeChunks = FieldGetter<MyOxygenCube, ConcurrentDictionary<Vector3I, int[]>>("m_dictionary");
        private static readonly Func<MyOxygenCube, MyOxygenRoomLink[]> CubeLinks = FieldGetter<MyOxygenCube, MyOxygenRoomLink[]>("m_links");
        private static readonly Func<MyOxygenCube, Vector3I> CubeCellSize = FieldGetter<MyOxygenCube, Vector3I>("m_cellSize");
        private static readonly Func<MyOxygenCube, Vector3I> CubeBaseOffset = FieldGetter<MyOxygenCube, Vector3I>("m_baseOffset");
        private static readonly Func<MyOxygenRoomLink, int> LinkSlot = FieldGetter<MyOxygenRoomLink, int>("SlotId");
        private static readonly Func<MyGridGasSystem, HashSet<Vector3I>> InitializedBlocks = Getter<HashSet<Vector3I>>("m_initializedBlocks");
        private static readonly Action<MyGridGasSystem, Vector3I> CheckPositionForEmptyRoom = Method<Action<MyGridGasSystem, Vector3I>>("CheckPositionForEmptyRoom");
        private static readonly Type GridContextType = typeof(MyGridGasSystem).Assembly.GetType("Sandbox.Game.GameSystems.MyCubeGridGasContext");

        private static readonly Vector3I[] Sides =
        {
            new Vector3I(1, 0, 0), new Vector3I(-1, 0, 0), new Vector3I(0, 1, 0),
            new Vector3I(0, -1, 0), new Vector3I(0, 0, 1), new Vector3I(0, 0, -1),
        };

        private sealed class Pace
        {
            public long NotBefore;
        }

        private static readonly ConditionalWeakTable<MyGridGasSystem, Pace> Paces = new ConditionalWeakTable<MyGridGasSystem, Pace>();

        private struct Cell
        {
            public Vector3I Position;
            public MySlimBlock Block;
        }

        /// <summary>The cube's cells read and written in its chunk arrays, as its <c>Add</c> and <c>TryGetValue</c> do.</summary>
        private struct CubeCells
        {
            private readonly ConcurrentDictionary<Vector3I, int[]> m_chunks;
            private readonly Vector3I m_cellSize;
            private readonly Vector3I m_baseOffset;
            private Vector3I m_lastKey;
            private int[] m_last;

            public CubeCells(MyOxygenCube cube)
            {
                m_chunks = CubeChunks(cube);
                m_cellSize = CubeCellSize(cube);
                m_baseOffset = CubeBaseOffset(cube);
                m_lastKey = default(Vector3I);
                m_last = null;
            }

            // (the chunk holding the cell, null while it has none, and the cell's index in it: as the cube's GetCellPosition)
            public int[] Chunk(Vector3I position, out int index)
            {
                var key = position;
                if (-m_baseOffset.X > key.X) key.X -= m_cellSize.X - 1;
                if (-m_baseOffset.Y > key.Y) key.Y -= m_cellSize.Y - 1;
                if (-m_baseOffset.Z > key.Z) key.Z -= m_cellSize.Z - 1;
                var chunk = m_baseOffset + ((key + m_baseOffset) / m_cellSize - Vector3I.One) * m_cellSize;
                var d = position - chunk;
                index = d.X + d.Y * m_cellSize.X + d.Z * m_cellSize.X * m_cellSize.Y;
                if (m_last != null && chunk == m_lastKey) return m_last;
                if (!m_chunks.TryGetValue(chunk, out var cells)) return null;
                m_lastKey = chunk;
                m_last = cells;
                return cells;
            }
        }

        [ThreadStatic] private static ulong[] _flooded;
        [ThreadStatic] private static ulong[] _occupied;
        [ThreadStatic] private static Queue<Cell> _queue;
        [ThreadStatic] private static long _refreshStarted;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("GasRoomRefresh", ctx, c =>
        {
            if (Context == null || Rooms == null || CubeRoom == null || RoomCellsBuffer == null || StoredMin == null || StoredMax == null
                || PreviousMin == null || PreviousMax == null || IsProcessing == null || Grid == null || CreateAirtightRoom == null || GridContextType == null
                || GridCubes == null || CubeChunks == null || CubeLinks == null || CubeCellSize == null || CubeBaseOffset == null || LinkSlot == null
                || InitializedBlocks == null || CheckPositionForEmptyRoom == null)
                throw new MissingMemberException("MyGridGasSystem, MyCubeGrid.m_cubes or MyOxygenCube: a field, Grid, CreateAirtightRoom or MyCubeGridGasContext");
            c.GetPattern(Find("cubeGrid_OnBlockAdded")).Prefixes.Add(Own(nameof(BlockAddedPrefix)));
            c.GetPattern(Find("StartRefreshRoomData")).Prefixes.Add(Own(nameof(StartRefreshPrefix)));
            var refresh = c.GetPattern(Find("RefreshRoomData"));
            refresh.Prefixes.Add(Own(nameof(RefreshPrefix)));
            refresh.Suffixes.Add(Own(nameof(RefreshSuffix)));
            c.GetPattern(Find("RefreshTopRoom")).Prefixes.Add(Own(nameof(RefreshTopRoomPrefix)));
            c.GetPattern(Find("GenerateEmptyRooms")).Prefixes.Add(Own(nameof(GenerateEmptyRoomsPrefix)));
        });

        private static MethodInfo Find(string name) =>
            typeof(MyGridGasSystem).GetMethod(name, Any) ?? throw new MissingMethodException("MyGridGasSystem." + name);

        private static MethodInfo Own(string name) => typeof(GasRoomRefresh).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        private static Func<MyGridGasSystem, T> Getter<T>(string name) => FieldGetter<MyGridGasSystem, T>(name);

        private static Func<TOwner, T> FieldGetter<TOwner, T>(string name)
        {
            var field = typeof(TOwner).GetField(name, Any);
            if (field == null || field.FieldType != typeof(T) && !(typeof(T) == typeof(object) && !field.FieldType.IsValueType)) return null;
            var method = new DynamicMethod("Get_" + name, typeof(T), new[] { typeof(TOwner) }, typeof(TOwner), true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<TOwner, T>)method.CreateDelegate(typeof(Func<TOwner, T>));
        }

        private static ulong[] Buffer(ref ulong[] buffer, int words)
        {
            if (buffer == null || buffer.Length < words) buffer = new ulong[Math.Max(words, (buffer?.Length ?? 0) * 2)];
            else Array.Clear(buffer, 0, words);
            return buffer;
        }

        private static Func<MyGridGasSystem, MyCubeGrid> GridGetter()
        {
            var get = typeof(MyUpdateableGridSystem).GetProperty("Grid", Any)?.GetGetMethod(true);
            if (get == null) return null;
            var method = new DynamicMethod("Get_Grid", typeof(MyCubeGrid), new[] { typeof(MyGridGasSystem) }, typeof(MyGridGasSystem), true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, get);
            il.Emit(OpCodes.Ret);
            return (Func<MyGridGasSystem, MyCubeGrid>)method.CreateDelegate(typeof(Func<MyGridGasSystem, MyCubeGrid>));
        }

        private static T Method<T>(string name) where T : Delegate
        {
            var method = typeof(MyGridGasSystem).GetMethod(name, Any);
            return method == null ? null : (T)Delegate.CreateDelegate(typeof(T), method, false);
        }

        // (true when the game's MyCubeGridGasContext is behind the system: a live grid, not a test seam)
        private static bool LiveGrid(MyGridGasSystem system) => Context(system)?.GetType() == GridContextType && Grid(system) != null;

        // ---- a block placed unfinished

        private static bool BlockAddedPrefix(MyGridGasSystem __instance, IMySlimBlock addedBlock)
        {
            if (!(addedBlock is MySlimBlock block) || !LiveGrid(__instance) || block.CubeGrid != Grid(__instance)) return true;
            if (Rooms(__instance) == null || CubeRoom(__instance) == null) return true;
            if (block.FatBlock is Sandbox.ModAPI.IMyDoor || block.FatBlock is IMyGasBlock) return true;
            var definition = block.BlockDefinition;
            var stages = definition?.BuildProgressModels;
            if (stages == null || stages.Length == 0 || block.BuildLevelRatio >= stages[stages.Length - 1].BuildRatioUpperBound) return true;
            // (as the game decides that the box has grown)
            var processing = IsProcessing(__instance);
            var min = processing ? PreviousMin(__instance) : StoredMin(__instance);
            var max = processing ? PreviousMax(__instance) : StoredMax(__instance);
            var grid = block.CubeGrid;
            if (Vector3I.Min(grid.Min - Vector3I.One, min) != min || Vector3I.Max(grid.Max + Vector3I.One, max) != max) return true;
            return false;
        }

        // ---- the pause between refreshes

        private static bool StartRefreshPrefix(MyGridGasSystem __instance)
        {
            if (IsProcessing(__instance) || !Paces.TryGetValue(__instance, out var pace)) return true;
            return Stopwatch.GetTimestamp() >= System.Threading.Volatile.Read(ref pace.NotBefore);
        }

        private static void RefreshPrefix()
        {
            _refreshStarted = Stopwatch.GetTimestamp();
        }

        private static void RefreshSuffix(MyGridGasSystem __instance)
        {
            var now = Stopwatch.GetTimestamp();
            var pause = Math.Min((now - _refreshStarted) * PauseFactor, Stopwatch.Frequency * MaxPauseMs / 1000);
            System.Threading.Volatile.Write(ref Paces.GetOrCreateValue(__instance).NotBefore, now + pause);
        }

        // ---- the outside, flooded

        // (as the game's RefreshTopRoom and GetRoomBlocks(m_storedGridMin, top room))
        private static bool RefreshTopRoomPrefix(MyGridGasSystem __instance)
        {
            if (!LiveGrid(__instance)) return true;
            var top = Rooms(__instance)[0];
            if (!top.IsDirty) return false;
            var min = StoredMin(__instance);
            var max = StoredMax(__instance);
            var size = max - min + Vector3I.One;
            if (size.X <= 0 || size.Y <= 0 || size.Z <= 0) return true;
            var cells = (long)size.X * size.Y * size.Z;
            if (cells > MaxCells) return true;

            var grid = Grid(__instance);
            var cube = CubeRoom(__instance);
            var cubes = GridCubes(grid);
            var chunks = CubeChunks(cube);
            var links = CubeLinks(cube);
            if (cubes == null || chunks == null || links == null) return true;
            var link = top.Link;

            var words = (int)((cells + 63) >> 6);
            var flooded = Buffer(ref _flooded, words);
            var occupied = Buffer(ref _occupied, words);
            // (the cells with a block, and only for those the block is looked up; a block added meanwhile is the next refresh's, as in the game)
            foreach (var entry in cubes)
            {
                var p = entry.Key;
                if (p.X >= min.X && p.Y >= min.Y && p.Z >= min.Z && p.X <= max.X && p.Y <= max.Y && p.Z <= max.Z)
                    Mark(occupied, Index(p, min, size));
            }
            var queue = _queue ?? (_queue = new Queue<Cell>());
            queue.Clear();

            var reached = 1;
            Mark(flooded, Index(min, min, size));
            cube.Add(min, link);
            var slotOfLink = LinkSlot(link);
            var cubeCells = new CubeCells(cube);
            queue.Enqueue(new Cell { Position = min, Block = IsMarked(occupied, Index(min, min, size)) ? grid.GetCubeBlock(min) : null });
            try
            {
                while (queue.Count > 0)
                {
                    var from = queue.Dequeue();
                    for (var i = 0; i < Sides.Length; i++)
                    {
                        var to = from.Position + Sides[i];
                        if (to.X < min.X || to.Y < min.Y || to.Z < min.Z || to.X > max.X || to.Y > max.Y || to.Z > max.Z) continue;
                        var index = Index(to, min, size);
                        if (IsMarked(flooded, index)) continue;
                        var block = IsMarked(occupied, index) ? grid.GetCubeBlock(to) : null;
                        if ((from.Block != null || block != null) && AirtightBetween(from.Block, from.Position, block, to)) continue;
                        Mark(flooded, index);
                        var chunkCells = cubeCells.Chunk(to, out var cellIndex);
                        if (chunkCells != null && slotOfLink != 0) chunkCells[cellIndex] = slotOfLink;
                        else cube.Add(to, link);
                        reached++;
                        queue.Enqueue(new Cell { Position = to, Block = block });
                    }
                }
            }
            finally
            {
                queue.Clear();
            }

            // (the outside's cells the flood did not reach: the game's GetBlocksOfRoom less the flooded cells, read off the cube in one pass;
            // the flood only wrote the outside's link into cells it reached, so these are the outside's cells from before it)
            var before = RoomCellsBuffer(__instance);
            before.Clear();
            links = CubeLinks(cube);
            var cellSize = CubeCellSize(cube);
            var layer = cellSize.X * cellSize.Y;
            foreach (var chunk in chunks)
            {
                var slots = chunk.Value;
                for (var i = 0; i < slots.Length; i++)
                {
                    var slot = slots[i];
                    if (slot == 0 || slot >= links.Length) continue;
                    var cellLink = links[slot];
                    if (cellLink == null || cellLink.Room != top) continue;
                    var z = i / layer;
                    var rest = i - z * layer;
                    var y = rest / cellSize.X;
                    var p = chunk.Key + new Vector3I(rest - y * cellSize.X, y, z);
                    if (p.X >= min.X && p.Y >= min.Y && p.Z >= min.Z && p.X <= max.X && p.Y <= max.Y && p.Z <= max.Z && IsMarked(flooded, Index(p, min, size))) continue;
                    before.Add(p);
                }
            }
            if (before.Count != 0)
            {
                var first = default(Vector3I);
                foreach (var p in before) { first = p; break; }
                CreateAirtightRoom(__instance, before, 0f, first).IsDirty = true;
            }
            top.BlockCount = reached;
            top.IsDirty = false;
            top.StartingPosition = min;
            return false;
        }

        // (as the game's GenerateEmptyRooms: every cell of the box in its order, the cells in a room passed over without the cube's lookups)
        private static bool GenerateEmptyRoomsPrefix(MyGridGasSystem __instance)
        {
            if (!LiveGrid(__instance)) return true;
            var cube = CubeRoom(__instance);
            var initialized = InitializedBlocks(__instance);
            var min = StoredMin(__instance);
            var max = StoredMax(__instance);
            var cubeCells = new CubeCells(cube);
            for (var z = min.Z; z <= max.Z; z++)
            for (var y = min.Y; y <= max.Y; y++)
            for (var x = min.X; x <= max.X; x++)
            {
                var p = new Vector3I(x, y, z);
                var cells = cubeCells.Chunk(p, out var index);
                if (cells != null)
                {
                    var slot = cells[index];
                    if (slot != 0)
                    {
                        var links = CubeLinks(cube);
                        if (slot < links.Length && links[slot]?.Room != null) continue;
                    }
                }
                if (initialized.Contains(p)) continue;
                CheckPositionForEmptyRoom(__instance, p);
            }
            return false;
        }

        private static long Index(Vector3I p, Vector3I min, Vector3I size) =>
            (p.X - min.X) + (long)size.X * ((p.Y - min.Y) + (long)size.Y * (p.Z - min.Z));

        private static void Mark(ulong[] bits, long index) => bits[index >> 6] |= 1UL << (int)(index & 63);

        private static bool IsMarked(ulong[] bits, long index) => (bits[index >> 6] & (1UL << (int)(index & 63))) != 0;

        // (as the game's MyCubeGridGasContext.IsAirtightBetweenPositions, with both blocks looked up already)
        private static bool AirtightBetween(MySlimBlock fromBlock, Vector3I from, MySlimBlock toBlock, Vector3I to)
        {
            if (fromBlock == toBlock)
                return fromBlock != null && fromBlock.BlockDefinition != null && AirtightFromDefinition(fromBlock.BlockDefinition, fromBlock.BuildLevelRatio) == true;
            if (fromBlock != null && MyGridGasSystem.IsAirtightBlock(fromBlock, from, to - from)) return true;
            return toBlock != null && MyGridGasSystem.IsAirtightBlock(toBlock, to, from - to);
        }

        // (as the game's MyGridGasSystem.IsAirtightFromDefinition)
        private static bool? AirtightFromDefinition(MyCubeBlockDefinition definition, float buildLevelRatio)
        {
            var stages = definition.BuildProgressModels;
            if (stages != null && stages.Length != 0 && buildLevelRatio < stages[stages.Length - 1].BuildRatioUpperBound) return false;
            return definition.IsAirTight;
        }
    }
}
