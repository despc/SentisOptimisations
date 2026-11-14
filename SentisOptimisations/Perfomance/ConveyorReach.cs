using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRage.Algorithms;
using VRage.Game;
using VRage.Game.Entity;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// "Can the conveyor bring this item from that inventory to this one" (<c>IMyInventory.CanTransferItemTo</c>,
    /// <c>IsConnectedTo</c>, a transfer through conveyors: SO's freezer for an assembler's resources, players' scripts,
    /// mods) with one search of the network per destination, not one per pair.
    ///
    /// Vanilla (<c>MyInventory.CanTransferTo</c> → <c>MyGridConveyorSystem.ComputeCanTransfer</c>) walks the whole network
    /// from the source for every question - it does not stop at the destination - and remembers at most 25 answers per
    /// source inventory, for the frame, whatever changes in it. A script asking about each of 300 cargo containers walked
    /// the network 300 times; so did the freezer's assembler pass on a big base (prod 12.10.2026, 16 ms of a frame).
    ///
    /// Here the network is walked once backwards from the destination (<c>FindReachableInverted</c>, the conveyor
    /// directions reversed; Keen's own reachability cache uses it) with the rules of a vanilla search: each block's access
    /// for the source's owner (an enemy's block passes nothing, an admin passes everywhere), a sorter's filter for the
    /// item, a trading connector closed, large conveyors only for a large item. Every source of that owner then gets its
    /// answer from the blocks found. The answer is vanilla's, exactly:
    ///  - vanilla does not ask the access of the source itself (its search starts there), but asks it of every other
    ///    block on the way, the destination included. Backwards it is the other way round, so a block the search finds
    ///    but may not pass is still taken as a source (not walked through), and the destination's access is asked apart;
    ///  - an inventory to an inventory of the same block: reachable, as in vanilla (the search starts on its target).
    ///
    /// The game's own background question goes the same way: a pull or push of a block (an assembler, a generator for
    /// ice, a turret for ammo, a refinery) asks <c>MyGridConveyorSystem.ComputeCanTransfer</c> for each pair on a worker
    /// (<c>TransferData.ComputeTransfer</c>) and keeps the answer until the network is recomputed - a whole walk of the
    /// network per pair. On a big base being welded or fought over its answers were dropped all the time, and these walks
    /// held four cores without a break and kept the workers so busy that the game thread waited for an entity being
    /// made, up to half a second (prod trace 14.11.2026: 142 s of walks in 35.7 s). ComputeCanTransfer itself is answered
    /// here, on any thread.
    ///
    /// An answer is kept while nothing it was made of has changed - checked, not trusted as vanilla's answers are - and
    /// for the frame on the game thread (a worker is given one of the last second):
    ///  - the network: any change of a conveyor line (working or not, its ends, merged, cut, reversed), of the lines of
    ///    a block, of a connector's or rotor's link, the game's own "recompute the network" signals;
    ///  - whose a block is: a block's owner or sharing, factions (members, war and peace, reputation);
    ///  - the sorters, connectors, rotors, hinges and pistons (and their heads) the search met: their state (a sorter on,
    ///    built, working, powered; a connector locked, to which, trading; a head on, which) is written down with the answer
    ///    and compared before it is given again - as the network's own changes are not always seen: a small method of
    ///    the game may be inlined into its caller, where no patch reaches it;
    ///  - whether the source's owner is an admin who may use any terminal: part of what an answer is kept under.
    /// Every thread asks the one store of answers under a lock; an answer, once made, is not changed (a new one replaces
    /// it), and a search uses the asking thread's own pathfinding, as the game's does (its search state is per thread).
    /// If any of the game's methods watched for this is missing, nothing here is patched: vanilla answers. "Conveyor reach cache" on the Performance tab switches it off and on at once (off: vanilla; what
    /// was kept is dropped, so nothing from before is given when it is on again).
    /// </summary>
    [PatchShim]
    public static class ConveyorReach
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static MethodInfo _pathfinding;
        private static Action<long> _setPlayer;
        private static Action<MyDefinitionId> _setItem;
        private static Func<MyDefinitionId, bool> _needsLargeTube;
        private static FieldInfo _accessField, _largeField;

        /// <summary>One search's answer; not changed once it is in the store.</summary>
        private sealed class Reach
        {
            public readonly HashSet<MyEntity> Sources = new HashSet<MyEntity>();
            /// <summary>The sorters and connectors the search met, and their state then.</summary>
            public readonly List<(MyCubeBlock Block, int State)> Watched = new List<(MyCubeBlock, int)>();
            public bool TargetPassable;
            /// <summary>The count of changes when the search began, and the frame.</summary>
            public long Epoch;
            public int Frame;
        }

        /// <summary>How old, in frames, an answer a worker is given may be (the game thread's: of this frame).</summary>
        public const int WorkerFrames = 60;

        /// <summary>Answers kept before the stale ones are thrown out.</summary>
        private const int PruneAt = 4096;

        private static readonly Dictionary<(IMyConveyorEndpoint, MyDefinitionId, long, bool), Reach> Answers = new Dictionary<(IMyConveyorEndpoint, MyDefinitionId, long, bool), Reach>();
        private static readonly object Gate = new object();
        [ThreadStatic] private static List<IMyConveyorEndpoint> _found;
        private static long _epoch;

        /// <summary>For a check: the game's own search, not this one, on this thread.</summary>
        [ThreadStatic] public static bool Vanilla;

        /// <summary>Searches of the network made, questions answered from them, and of those, asked off the game thread (since the start).</summary>
        public static long Searches, Answered, AnsweredOffGameThread;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("ConveyorReach", ctx, _ =>
        {
            var system = typeof(MyGridConveyorSystem);
            // the game's one pathfinding system, made on first use (a property): taken when asked, locked as the game locks it
            _pathfinding = system.GetProperty("Pathfinding", Static)?.GetGetMethod(true) ?? throw new MissingMemberException("MyGridConveyorSystem.Pathfinding");
            _setPlayer = (Action<long>)Delegate.CreateDelegate(typeof(Action<long>), Method(system, "SetTraversalPlayerId", Static));
            _setItem = (Action<MyDefinitionId>)Delegate.CreateDelegate(typeof(Action<MyDefinitionId>), Method(system, "SetTraversalInventoryItemDefinitionId", Static));
            _needsLargeTube = (Func<MyDefinitionId, bool>)Delegate.CreateDelegate(typeof(Func<MyDefinitionId, bool>), Method(system, "NeedsLargeTube", Static));
            _accessField = system.GetField("IsAccessAllowedPredicate", Static) ?? throw new MissingFieldException("MyGridConveyorSystem.IsAccessAllowedPredicate");
            _largeField = system.GetField("IsConveyorLargePredicate", Static) ?? throw new MissingFieldException("MyGridConveyorSystem.IsConveyorLargePredicate");
            var question = typeof(MyInventory).GetMethod("CanTransferTo", BindingFlags.Instance | BindingFlags.NonPublic, null,
                               new[] { typeof(MyInventory), typeof(MyDefinitionId?) }, null)
                           ?? throw new MissingMethodException("MyInventory.CanTransferTo");

            // everything an answer is made of, each change of it seen (all found before anything is patched)
            var line = typeof(MyConveyorLine);
            var watched = new List<MethodInfo>
            {
                Method(system, nameof(MyGridConveyorSystem.FlagForRecomputation), Instance),
                Method(system, nameof(MyGridConveyorSystem.UpdateLines), Instance),
                // (the small methods below may be inlined into their callers, where a patch does not reach them: the
                // callers, big enough not to be, are watched too)
                Method(system, nameof(MyGridConveyorSystem.AddConveyorBlock), Instance), Method(system, nameof(MyGridConveyorSystem.RemoveConveyorBlock), Instance),
                Method(system, nameof(MyGridConveyorSystem.AddSegmentBlock), Instance), Method(system, nameof(MyGridConveyorSystem.RemoveSegmentBlock), Instance),
                Method(system, "AddSegmentBlockInternal", Instance), Method(system, "TryMergeEndpointSegment", Instance),
                Method(system, "TryMergeEndpointEndpoint", Instance), Method(system, "MergeSegmentSegment", Instance),
                Method(system, "UpdateLineReferences", Instance), Method(system, nameof(MyGridConveyorSystem.ToggleConnectors), Instance),
                Method(system, nameof(MyGridConveyorSystem.AfterBlockDeserialization), Instance), Method(system, nameof(MyGridConveyorSystem.AfterGridClose), Instance),
                Method(line, "UpdateIsFunctional", Instance), Method(line, "UpdateIsWorking", Instance),
                Method(line, "DisconnectEndpoint", Instance), Method(line, "SetEndpoint", Instance), Method(line, "InitEndpoints", Instance),
                Method(line, "Merge", Instance), Method(line, "MergeInternal", Instance), Method(line, "RemovePortion", Instance),
                Method(line, "RecalculateConductivity", Instance), Method(line, "Reverse", Instance),
                Method(typeof(MyMultilineConveyorEndpoint), "SetConveyorLine", Instance),
                Method(typeof(MyAttachableConveyorEndpoint), "AddAttachableLine", Instance),
                Method(typeof(MyAttachableConveyorEndpoint), "RemoveAttachableLine", Instance),
                Method(typeof(MyAttachableConveyorEndpoint), "DetachAll", Instance),
                Method(typeof(MyCubeBlock), nameof(MyCubeBlock.ChangeOwner), Instance),
            };
            var factions = typeof(MyFactionCollection);
            foreach (var name in new[]
                     {
                         "DeleteFactionRelations", "DeletePlayerRelations", "AddPlayerToFactionInternal", "ClearPlayersFactionMapping",
                         "ApplyFactionStateChange", "ChangeFactionRelation", "ChangeReputationBetweenFactions", "ChangeRelationWithPlayer",
                         "ChangeReputationWithPlayer", "RemoveRelation",
                     })
                watched.Add(Method(factions, name, Instance));
            foreach (var init in line.GetMethods(Instance))
                if (init.Name == "Init" && init.DeclaringType == line) watched.Add(init);
            var working = line.GetProperty(nameof(MyConveyorLine.IsWorking))?.GetSetMethod(true) ?? throw new MissingMethodException("MyConveyorLine.IsWorking set");

            var harmony = SentisOptimisationsPlugin.CrashFix.CrashFixPatch.harmony;
            var changed = new HarmonyMethod(typeof(ConveyorReach).GetMethod(nameof(Changed), Static));
            foreach (var method in watched) harmony.Patch(method, postfix: changed);
            harmony.Patch(working, prefix: new HarmonyMethod(typeof(ConveyorReach).GetMethod(nameof(WorkingPrefix), Static)));
            harmony.Patch(question, prefix: new HarmonyMethod(typeof(ConveyorReach).GetMethod(nameof(CanTransferToPrefix), Static)));
            var compute = system.GetMethod(nameof(MyGridConveyorSystem.ComputeCanTransfer), Static, null,
                              new[] { typeof(IMyConveyorEndpointBlock), typeof(IMyConveyorEndpointBlock), typeof(MyDefinitionId?) }, null)
                          ?? throw new MissingMethodException("MyGridConveyorSystem.ComputeCanTransfer");
            harmony.Patch(compute, prefix: new HarmonyMethod(typeof(ConveyorReach).GetMethod(nameof(ComputeCanTransferPrefix), Static)));
        });

        private static MethodInfo Method(Type type, string name, BindingFlags flags)
        {
            MethodInfo found = null;
            foreach (var method in type.GetMethods(flags))
            {
                if (method.Name != name || method.DeclaringType != type) continue;
                if (found != null) throw new AmbiguousMatchException($"{type.Name}.{name}");
                found = method;
            }
            return found ?? throw new MissingMethodException($"{type.Name}.{name}");
        }

        private static void Changed() => System.Threading.Interlocked.Increment(ref _epoch);

        private static void WorkingPrefix(MyConveyorLine __instance, bool value)
        {
            if (__instance.IsWorking != value) Changed();
        }

        private static IMyConveyorEndpointBlock EndpointOf(MyEntity entity)
        {
            if (entity is IMyConveyorEndpointBlock block) return block;
            return entity != null && entity.Components.TryGet<IMyConveyorEndpointBlock>(out var component) ? component : null;
        }

        private static bool CanTransferToPrefix(MyInventory __instance, MyInventory dstInventory, MyDefinitionId? itemType, ref bool __result)
        {
            if (!On()) return true;
            var source = EndpointOf(__instance.Owner);
            var target = EndpointOf(dstInventory?.Owner);
            if (source?.ConveyorEndpoint?.CubeBlock == null || target?.ConveyorEndpoint?.CubeBlock == null) return true;
            __result = Reaches(source, target, itemType);
            return false;
        }

        /// <summary>The game's own question, from the game thread or a worker (a block's pull or push).</summary>
        private static bool ComputeCanTransferPrefix(IMyConveyorEndpointBlock start, IMyConveyorEndpointBlock end, MyDefinitionId? itemId, ref bool __result)
        {
            if (!On()) return true;
            if (start?.ConveyorEndpoint?.CubeBlock == null || end?.ConveyorEndpoint?.CubeBlock == null) return true;
            __result = Reaches(start, end, itemId);
            return false;
        }

        private static bool On()
        {
            if (SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config?.ConveyorReach != true)
            {
                Changed();        // nothing kept from before it was off
                return false;
            }
            return !Vanilla && MySession.Static != null && MySandboxGame.Static != null;
        }

        /// <summary>What of a sorter, a connector or a mechanical block a search depends on, besides whose it is.</summary>
        private static int StateOf(MyCubeBlock block)
        {
            switch (block)
            {
                case MyConveyorSorter sorter:
                    return (sorter.Enabled ? 1 : 0) | (sorter.IsFunctional ? 2 : 0) | (sorter.IsWorking ? 4 : 0) |
                           (sorter.ResourceSink != null && sorter.ResourceSink.IsPoweredByType(MyResourceDistributorComponent.ElectricityId) ? 8 : 0);
                case MyShipConnector connector:
                    return ((connector.TradingEnabled.Value ? 1 : 0) | (connector.Connected ? 2 : 0)) ^ ((connector.Other?.EntityId.GetHashCode() ?? 0) << 2);
                case Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase stator:
                    return stator.TopBlock?.EntityId.GetHashCode() ?? 0;
                case Sandbox.Game.Entities.Blocks.MyAttachableTopBlockBase head:
                    return head.Stator?.EntityId.GetHashCode() ?? 0;
                default:
                    return 0;
            }
        }

        private static bool IsWatched(MyCubeBlock block) =>
            block is MyConveyorSorter || block is MyShipConnector || block is Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase ||
            block is Sandbox.Game.Entities.Blocks.MyAttachableTopBlockBase;

        /// <summary>As the game's access check: an admin who may use any terminal passes every block.</summary>
        private static bool AdminTerminals(long identity)
        {
            var player = MySession.Static.Players.TryGetPlayer(identity);
            return player != null && MySession.Static.RemoteAdminSettings.TryGetValue(player.Client.SteamUserId, out var settings) &&
                   settings.HasFlag(AdminSettingsEnum.UseTerminals);
        }

        /// <summary>The vanilla answer of ComputeCanTransfer(source, target, item), from one backward search per target.</summary>
        public static bool Reaches(IMyConveyorEndpointBlock source, IMyConveyorEndpointBlock target, MyDefinitionId? item)
        {
            var from = source.ConveyorEndpoint.CubeBlock;
            var to = target.ConveyorEndpoint;
            // vanilla's search starts on the source and takes any block of the target's as found
            if (from == to.CubeBlock) return true;

            var frame = MySession.Static.GameplayFrameCounter;
            var epoch = System.Threading.Interlocked.Read(ref _epoch);
            var onGame = System.Threading.Thread.CurrentThread == MySandboxGame.Static.UpdateThread;
            var owner = from.OwnerId;
            var key = (to, item ?? default(MyDefinitionId), owner, AdminTerminals(owner));
            Reach answer;
            lock (Gate) Answers.TryGetValue(key, out answer);
            if (!Fresh(answer, epoch, frame, onGame))
            {
                answer = new Reach { Epoch = epoch, Frame = frame };
                Search(to, item, owner, answer);
                lock (Gate)
                {
                    if (Answers.Count >= PruneAt) Prune(epoch, frame);
                    Answers[key] = answer;
                }
            }
            System.Threading.Interlocked.Increment(ref Answered);
            if (!onGame) System.Threading.Interlocked.Increment(ref AnsweredOffGameThread);
            return answer.TargetPassable && answer.Sources.Contains(from);
        }

        /// <summary>
        /// Whether an answer may be given: nothing changed since its search began, of this frame for the game thread (of
        /// the last <see cref="WorkerFrames"/> for a worker), and the sorters, connectors and mechanical blocks it met as
        /// they were.
        /// </summary>
        private static bool Fresh(Reach answer, long epoch, int frame, bool onGame)
        {
            if (answer == null || answer.Epoch != epoch) return false;
            if (onGame ? answer.Frame != frame : frame < answer.Frame || frame - answer.Frame > WorkerFrames) return false;
            foreach (var (block, state) in answer.Watched)
                if (StateOf(block) != state) return false;
            return true;
        }

        /// <summary>Throws out the answers no thread may be given any more (under the lock).</summary>
        private static void Prune(long epoch, int frame)
        {
            var stale = new List<(IMyConveyorEndpoint, MyDefinitionId, long, bool)>();
            foreach (var pair in Answers)
                if (pair.Value.Epoch != epoch || frame - pair.Value.Frame > WorkerFrames) stale.Add(pair.Key);
            foreach (var key in stale) Answers.Remove(key);
            // all of the last second, and still too many: begin anew
            if (Answers.Count >= PruneAt) Answers.Clear();
        }

        /// <summary>How many answers are kept, for the stand.</summary>
        public static int Kept()
        {
            lock (Gate) return Answers.Count;
        }

        private static void Search(IMyConveyorEndpoint target, MyDefinitionId? item, long owner, Reach answer)
        {
            System.Threading.Interlocked.Increment(ref Searches);
            var found = _found ?? (_found = new List<IMyConveyorEndpoint>());
            // the asking thread's own pathfinding (the game's is per thread), locked as the game locks it
            lock (_pathfinding.Invoke(null, null))
            {
                // the rules of ComputeCanTransfer: the source's owner, the item (for sorters), large conveyors for a large item
                _setPlayer(owner);
                _setItem(item ?? default(MyDefinitionId));
                var access = (Predicate<IMyConveyorEndpoint>)_accessField.GetValue(null);
                var edges = item.HasValue && _needsLargeTube(item.Value) ? (Predicate<IMyPathEdge<IMyConveyorEndpoint>>)_largeField.GetValue(null) : null;
                var sources = answer.Sources;
                var met = answer.Watched;
                if (IsWatched(target.CubeBlock)) met.Add((target.CubeBlock, StateOf(target.CubeBlock)));
                answer.TargetPassable = access(target);
                found.Clear();
                try
                {
                    MyGridConveyorSystem.FindReachableInverted(target, found, null, e =>
                    {
                        var block = e?.CubeBlock;
                        if (block != null && IsWatched(block)) met.Add((block, StateOf(block)));
                        if (access(e)) return true;
                        // a block the search may not pass can still be where an item starts from: kept, not walked through
                        if (block != null) sources.Add(block);
                        return false;
                    }, edges);
                    foreach (var e in found)
                        if (e?.CubeBlock != null) sources.Add(e.CubeBlock);
                }
                finally
                {
                    found.Clear();
                }
            }
        }
    }
}
