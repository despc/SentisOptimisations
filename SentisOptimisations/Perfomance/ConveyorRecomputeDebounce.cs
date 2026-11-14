using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using NLog;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// A grid being welded does not have its conveyor network recomputed after every block: the recompute waits until
    /// no block has been added for <see cref="QuietFrames"/> (3 s), and not longer than <see cref="MostFrames"/> (30 s).
    ///
    /// Vanilla flags the network of the whole physical group for a recompute (<c>FlagForRecomputation</c>) when a conveyor
    /// block or tube is added and whenever the owners of a grid are counted again - after every block with an owner added
    /// or lost. The recompute (<c>UpdateAfterSimulation100</c> → <c>RecomputeConveyorEndpoints</c>) first throws away every
    /// block's pull and push lists and the answers kept with them, then walks the whole network from every block, two
    /// passes in slices of 10 ms on a worker; a new flag aborts a recompute under way and starts it again. On a big base
    /// being welded it never ended: the lists stayed empty, and each pull and push of an assembler, a generator or a turret
    /// asked a whole walk of the network on a worker again, the walks held four cores and the workers were too busy for
    /// the entities being made, which the game thread waited for (prod trace 14.11.2026: the network recomputed every
    /// second, 142 s of walks in 35.7 s, the game thread waiting up to half a second).
    ///
    /// Only an addition waits: a block added (a conveyor block or tube), a conveyor line that has begun to work (a block
    /// on it welded up to working, two lines merged by a new block - what the game does with every conveyor welded on), and
    /// the owners counted again with no block's owner changed by hand. Everything else recomputes at once, as in vanilla:
    /// a conveyor block or tube removed, a conveyor line that has stopped working (its power gone, a block on it damaged,
    /// cut in two), a sorter, a connector, a rotor or piston, the conveyor system's power, a block given to someone else. While it waits the lists from before stay: a block just welded pulls and pushes nothing through them
    /// yet, the rest of the base works as before. (The answers "can this reach that" are SO's ConveyorReach, which follows
    /// the network as it is, any change at once.)
    /// </summary>
    [PatchShim]
    public static class ConveyorRecomputeDebounce
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>Frames with no block added before a waiting recompute is made (3 s).</summary>
        public const int QuietFrames = 180;

        /// <summary>The longest a recompute waits, however long the welding goes on (30 s).</summary>
        public const int MostFrames = 1800;

        private enum Cause { None, Addition }

        [ThreadStatic] private static Cause _cause;
        /// <summary>The conveyor line whose working state is being updated on this thread.</summary>
        [ThreadStatic] private static MyConveyorLine _line;

        private static readonly object Gate = new object();
        private static readonly HashSet<MyGridConveyorSystem> LinesByAddition = new HashSet<MyGridConveyorSystem>();
        private static readonly HashSet<MyGridConveyorSystem> LinesAtOnce = new HashSet<MyGridConveyorSystem>();
        private static readonly HashSet<MyCubeGrid> OwnerChanged = new HashSet<MyCubeGrid>();
        private static readonly Dictionary<MyCubeGrid, (int First, int Last)> Waiting = new Dictionary<MyCubeGrid, (int, int)>();
        private static readonly List<MyCubeGrid> Due = new List<MyCubeGrid>();

        private static Func<MyGridConveyorSystem, MyCubeGrid> _gridOf;
        private static Action<MyGridConveyorSystem, bool> _setNeedsRecomputation;
        private static DateTime _lastLog = DateTime.UtcNow;

        /// <summary>Recomputes put off, made at once, and made after waiting (since the start).</summary>
        public static long Deferred, AtOnce, Released;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("ConveyorRecomputeDebounce", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            var system = typeof(MyGridConveyorSystem);
            var systems = typeof(MyCubeGridSystems);
            MethodInfo Own(string name) => typeof(ConveyorRecomputeDebounce).GetMethod(name, statics);
            MethodInfo Game(Type type, string name) => type.GetMethod(name, instance | BindingFlags.DeclaredOnly) ?? throw new MissingMethodException(type.Name, name);

            // everything bound before anything is patched: a member gone leaves vanilla
            var grid = typeof(MyUpdateableGridSystem).GetProperty("Grid", instance)?.GetGetMethod(true) ?? throw new MissingMemberException("MyUpdateableGridSystem.Grid");
            var needs = system.GetField("m_needsRecomputation", instance) ?? throw new MissingFieldException("MyGridConveyorSystem.m_needsRecomputation");
            var self = Expression.Parameter(system);
            var value = Expression.Parameter(typeof(bool));
            _gridOf = Expression.Lambda<Func<MyGridConveyorSystem, MyCubeGrid>>(Expression.Call(self, grid), self).Compile();
            _setNeedsRecomputation = Expression.Lambda<Action<MyGridConveyorSystem, bool>>(Expression.Assign(Expression.Field(self, needs), value), self, value).Compile();
            var flag = Game(system, nameof(MyGridConveyorSystem.FlagForRecomputation));
            var update = Game(system, "Update");
            var powered = Game(system, "Receiver_IsPoweredChanged");
            var added = Game(systems, nameof(MyCubeGridSystems.OnBlockAdded));
            var removed = Game(systems, nameof(MyCubeGridSystems.OnBlockRemoved));
            var ownership = Game(systems, nameof(MyCubeGridSystems.OnBlockOwnershipChanged));
            var changeOwner = Game(typeof(MyCubeBlock), nameof(MyCubeBlock.ChangeOwner));
            var lineWorking = Game(typeof(MyConveyorLine), nameof(MyConveyorLine.UpdateIsWorking));

            // OnBlockAdded carries a Torch patch of SO already: Torch here too (two kinds on one method, only one works);
            // the conveyor system's and the ownership's are patched with Harmony, as ConveyorReach patches them
            ctx.GetPattern(added).Prefixes.Add(Own(nameof(BlockAddedPrefix)));
            var harmony = SentisOptimisationsPlugin.CrashFix.CrashFixPatch.harmony;
            harmony.Patch(flag, prefix: new HarmonyMethod(Own(nameof(FlagPrefix))));
            harmony.Patch(update, prefix: new HarmonyMethod(Own(nameof(LinesPrefix))), finalizer: new HarmonyMethod(Own(nameof(ClearCause))));
            harmony.Patch(powered, prefix: new HarmonyMethod(Own(nameof(PoweredPrefix))));
            harmony.Patch(removed, prefix: new HarmonyMethod(Own(nameof(BlockRemovedPrefix))));
            harmony.Patch(ownership, prefix: new HarmonyMethod(Own(nameof(OwnershipPrefix))), finalizer: new HarmonyMethod(Own(nameof(ClearCause))));
            harmony.Patch(changeOwner, postfix: new HarmonyMethod(Own(nameof(ChangeOwnerPostfix))));
            harmony.Patch(lineWorking, prefix: new HarmonyMethod(Own(nameof(LineWorkingPrefix))), finalizer: new HarmonyMethod(Own(nameof(LineWorkingFinalizer))));
        }

        /// <summary>As vanilla's UpdateConveyorLines: a block that changes the conveyor lines.</summary>
        private static bool OfConveyors(MySlimBlock block)
        {
            var fat = block?.FatBlock;
            return fat != null && (fat is IMyConveyorEndpointBlock || fat is Sandbox.ModAPI.IMyConveyorTube || fat.Components.TryGet<IMyConveyorEndpointBlock>(out _));
        }

        private static void BlockAddedPrefix(MyCubeGridSystems __instance, MySlimBlock block)
        {
            var system = __instance?.ConveyorSystem;
            if (system == null || !OfConveyors(block)) return;
            lock (Gate) LinesByAddition.Add(system);
        }

        private static void BlockRemovedPrefix(MyCubeGridSystems __instance, MySlimBlock block)
        {
            var system = __instance?.ConveyorSystem;
            if (system == null || !OfConveyors(block)) return;
            lock (Gate) LinesAtOnce.Add(system);
        }

        private static void PoweredPrefix(MyGridConveyorSystem __instance)
        {
            lock (Gate) LinesAtOnce.Add(__instance);
        }

        /// <summary>The lines asked for by additions only flag the network as an addition; any other, or none known, at once.</summary>
        private static void LinesPrefix(MyGridConveyorSystem __instance)
        {
            if (!__instance.NeedsUpdateLines) return;
            bool atOnce, byAddition;
            lock (Gate)
            {
                atOnce = LinesAtOnce.Remove(__instance);
                byAddition = LinesByAddition.Remove(__instance);
            }
            _cause = byAddition && !atOnce ? Cause.Addition : Cause.None;
        }

        private static Exception ClearCause(Exception __exception)
        {
            _cause = Cause.None;
            return __exception;
        }

        // MyConveyorLine.UpdateIsWorking flags the network when the line's working state flips (IsWorking set, then the flag)
        private static void LineWorkingPrefix(MyConveyorLine __instance) => _line = __instance;

        private static Exception LineWorkingFinalizer(Exception __exception)
        {
            _line = null;
            return __exception;
        }

        private static void ChangeOwnerPostfix(MyCubeBlock __instance)
        {
            var grid = __instance?.CubeGrid;
            if (grid == null) return;
            lock (Gate) OwnerChanged.Add(grid);
        }

        /// <summary>The owners counted again: an addition, unless a block's owner was changed (its access is another now).</summary>
        private static void OwnershipPrefix(MyCubeGrid cubeGrid)
        {
            bool changed;
            lock (Gate) changed = cubeGrid != null && OwnerChanged.Remove(cubeGrid);
            _cause = changed ? Cause.None : Cause.Addition;
        }

        private static bool FlagPrefix(MyGridConveyorSystem __instance)
        {
            try
            {
                // a line that has begun to work (a block welded onto it, two lines merged): an addition; one that stopped
                // (power gone, a block damaged, a line cut): at once
                var cause = _line != null ? (_line.IsWorking ? Cause.Addition : Cause.None) : _cause;
                var grid = _gridOf(__instance);
                var group = grid == null ? null : MyGridPhysicalHierarchy.Static?.GetGroup(grid);
                if (group == null || MySession.Static == null) return true;
                var now = MySession.Static.GameplayFrameCounter;
                lock (Gate)
                {
                    if (cause == Cause.Addition)
                    {
                        // vanilla's own walk of the group, the flag left for later
                        foreach (var node in group.Nodes)
                        {
                            var nodeGrid = node.NodeData;
                            if (nodeGrid == null) continue;
                            Waiting[nodeGrid] = Waiting.TryGetValue(nodeGrid, out var was) ? (was.First, now) : (now, now);
                        }
                        Deferred++;
                        return false;
                    }
                    // recomputed now: the whole group, nothing of it waits any more
                    foreach (var node in group.Nodes)
                        if (node.NodeData != null) Waiting.Remove(node.NodeData);
                }
                AtOnce++;
                return true;
            }
            catch (Exception e)
            {
                Log.Error(e, "ConveyorRecomputeDebounce: a recompute was not looked at");
                return true;
            }
        }

        /// <summary>Game thread, every frame: the recomputes whose welding has stopped (or waited the longest) flagged.</summary>
        public static void Tick()
        {
            var session = MySession.Static;
            if (session == null) return;
            var now = session.GameplayFrameCounter;
            if (now % 10 != 0) return;
            lock (Gate)
            {
                if (Waiting.Count == 0) return;
                foreach (var pair in Waiting)
                    if (pair.Key.Closed || now - pair.Value.Last >= QuietFrames || now - pair.Value.First >= MostFrames || now < pair.Value.First)
                        Due.Add(pair.Key);
                foreach (var grid in Due) Waiting.Remove(grid);
            }
            try
            {
                foreach (var grid in Due)
                {
                    var system = grid.Closed ? null : grid.GridSystems?.ConveyorSystem;
                    if (system == null) continue;
                    _setNeedsRecomputation(system, true);
                    Released++;
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "ConveyorRecomputeDebounce: a waiting recompute was not flagged");
            }
            finally
            {
                Due.Clear();
            }
            if (global::SentisOptimisations.DiagLog.On && DateTime.UtcNow - _lastLog > TimeSpan.FromMinutes(1))
            {
                _lastLog = DateTime.UtcNow;
                Log.Info($"ConveyorRecomputeDebounce: {Deferred} recomputes put off, {Released} made after waiting, {AtOnce} at once; {WaitingCount()} grids waiting");
            }
        }

        /// <summary>Grids whose recompute waits now.</summary>
        public static int WaitingCount()
        {
            lock (Gate) return Waiting.Count;
        }

        /// <summary>Whether the grid's recompute waits now (for the stand).</summary>
        public static bool IsWaiting(MyCubeGrid grid)
        {
            lock (Gate) return grid != null && Waiting.ContainsKey(grid);
        }
    }
}
