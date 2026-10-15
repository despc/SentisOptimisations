using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using VRage.Library;
using VRage.Library.Utils;
using VRage.Network;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Sends inventories only to clients that can see them.
    ///
    /// The game keeps an inventory delta per client per inventory and sends it as often as the
    /// client's update layer allows - every four frames for anything nearby - to every client that
    /// has the grid, whether or not that client has any use for it. The game itself says the client
    /// has none: MyEntityInventoryStateGroup.GetPriorityStateGroup gives the group priority 1 for the
    /// client that has the block open and 0 for everyone else, but MyReplicationServer.FilterStateSync
    /// never reads that priority and sends whatever is queued. On the replication test 64 clients who
    /// never opened a terminal still made half a million inventory serializations a minute, and the
    /// frames where many of them line up are the worst frames of the run: 73 ms of a 96 ms frame.
    ///
    /// A client with nothing open in front of it (no context entity) gets the inventories of grids
    /// farther than <see cref="NearDistance"/> every <see cref="IdleSendIntervalFrames"/> frames
    /// instead of every few. Nothing is dropped: the delta
    /// is computed against what that client last received, so the next update carries everything that
    /// changed meanwhile. The moment the client opens something, all of its inventories are pulled to
    /// the front of its queue and it is served at the normal rate again, so opening a terminal shows
    /// the contents without the idle wait. The client's own character and the entity it controls are
    /// never held back, and neither is anything but inventories.
    ///
    /// An inventory held back is parked off the client's queue (<c>MyClient.DirtyQueue</c>, a binary heap): on the
    /// replication test the held-back inventories were 13 800 of the 14 500 entries of each client's heap, and every
    /// operation on it - the frame's due entries taken out, each change put in - went through all those levels. Parked,
    /// it waits in the client's list of the second it is due in (<see cref="ClientView"/>) and is put in the queue by
    /// the game's own scheduling then, or at once when the client opens something. A parked entry whose group the
    /// client no longer has (the replicable left it) is dropped.
    /// </summary>
    [PatchShim]
    public static class IdleInventorySync
    {
        /// <summary>
        /// How rarely, on average, an inventory goes to a client that has nothing open, in frames
        /// (ten minutes). Only a safety net for whatever shows inventories without a context entity:
        /// opening something is served at once, and whatever is near the client is not held back.
        /// Every inventory that changes goes to every client once per interval, so the interval is
        /// what the traffic is: a base of busy refineries seen by 64 clients is ~640 thousand
        /// inventory/client pairs, and at one minute that was still ~560 thousand writes and 210 MB of
        /// garbage a minute.
        /// </summary>
        public const long IdleSendIntervalFrames = 36000;

        /// <summary>Inventories of grids closer than this to the client, in metres, are sent at the normal rate.</summary>
        public const double NearDistance = 100;

        public static long Delayed;
        public static long Kept;
        public static long Normal;
        public static long WokenUp;

        private static readonly Type ServerType = typeof(MyReplicationServer);
        private static readonly Type ClientType = ServerType.Assembly.GetType("VRage.Network.MyClient", true);
        private static readonly Type InventoryGroupType = typeof(MyCubeGrid).Assembly
            .GetType("Sandbox.Game.Replication.StateGroups.MyEntityInventoryStateGroup", true);
        private static readonly FieldInfo ClientStatesField =
            ServerType.GetField("m_clientStates", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Func<object, MyClientStateBase> ClientState = BuildClientField<MyClientStateBase>("State");
        private static readonly Func<object, object> ReplicableToLayer = BuildClientField<object>("ReplicableToLayer");
        private static readonly Func<object, Dictionary<IMyStateGroup, MyStateDataEntry>> StateGroups =
            BuildClientField<Dictionary<IMyStateGroup, MyStateDataEntry>>("StateGroups");
        private static readonly Func<object, FastPriorityQueue<MyStateDataEntry>> DirtyQueue =
            BuildClientField<FastPriorityQueue<MyStateDataEntry>>("DirtyQueue");
        private static readonly Action<MyReplicationServer, object, MyStateDataEntry, long, bool> ScheduleSync = BuildScheduleSync();
        private static readonly Func<MyReplicationServer, long> SyncFrame = BuildSyncFrame();
        private static readonly Func<MyStateDataEntry, long> EntryPriority = BuildEntryPriority();
        private static readonly Func<object, IMyReplicable, int> LayerSendInterval = BuildLayerSendInterval();
        private static readonly Func<MyReplicationServer, IMyReplicable, bool> Postponed = BuildPostponed();
        /// <summary>The server seen by the last frame, so a test can ask about the queues.</summary>
        private static MyReplicationServer _server;

        /// <summary>What this frame knows about each client; drops with the client.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, ClientView> Views =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, ClientView>();
        [ThreadStatic] private static bool _rescheduling;

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("IdleInventorySync", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var self = typeof(IdleInventorySync);
            const BindingFlags anyStatic = BindingFlags.Static | BindingFlags.NonPublic;
            ctx.GetPattern(ServerType.GetMethod("ScheduleStateGroupSync", instance))
                .Prefixes.Add(self.GetMethod(nameof(ScheduleStateGroupSyncPrefix), anyStatic));
            ctx.GetPattern(ServerType.GetMethod("SendUpdate", instance))
                .Prefixes.Add(self.GetMethod(nameof(SendUpdatePrefix), anyStatic));
        }

        private static bool ScheduleStateGroupSyncPrefix(MyReplicationServer __instance, object client,
            MyStateDataEntry groupEntry, long currentTime, bool allowReplicableRemoval)
        {
            if (_rescheduling || groupEntry?.Group == null || groupEntry.Group.GetType() != InventoryGroupType)
            {
                Normal++;
                return !Schedule(__instance, client, groupEntry, currentTime);
            }
            if (!ShouldDelay(client, groupEntry))
            {
                // served the normal way now (the client opened something, came near): out of the parking
                if (Views.TryGetValue(client, out var near)) near.Parked.Remove(groupEntry);
                Normal++;
                return !Schedule(__instance, client, groupEntry, currentTime);
            }
            var view = Views.GetOrCreateValue(client);
            // Already waiting for its turn: leave it there. The game moves a queued entry to whichever
            // time is sooner, and every change of a busy inventory schedules it again, so with a new
            // random delay each time the soonest of dozens of draws won and the idle interval was
            // never reached (measured: 768 thousand inventory writes a minute at 1800 frames, the
            // same as at 600).
            if (view.Parked.ContainsKey(groupEntry) || DirtyQueue(client).Contains(groupEntry))
            {
                Kept++;
                return false;
            }
            // Parked till a later frame, then the game's own scheduling, so the layer's interval, its random
            // spread and the removal of replicables that left the layer all still apply.
            Delayed++;
            // Spread over the whole idle interval: shifting every inventory by the same amount only
            // moves the pile-up, and a frame that serves a client's 400 inventories at once is
            // exactly what this is meant to prevent.
            view.Park(groupEntry, currentTime + MyRandom.Instance.Next(1, (int)(IdleSendIntervalFrames * 2)));
            return false;
        }

        [ThreadStatic] private static object _lastClient;
        [ThreadStatic] private static ClientView _lastView;

        /// <summary>
        /// The game's ScheduleStateGroupSync for a replicable whose layer the client knows, with the client's controlled
        /// and character replicables as read once this frame (<see cref="SendUpdatePrefix"/>): the game reads both on
        /// every call, each through the player and its entity in dictionaries - in the replication test's join that was
        /// a tenth of the network tick. False leaves the call to the game: a client not seen this frame yet, a replicable
        /// with no layer (the game decides whether it leaves the client).
        /// </summary>
        private static bool Schedule(MyReplicationServer server, object client, MyStateDataEntry entry, long currentTime)
        {
            var owner = entry?.Owner;
            if (owner == null || entry.Group == null) return false;
            ClientView view;
            if (ReferenceEquals(client, _lastClient)) view = _lastView;
            else
            {
                if (!Views.TryGetValue(client, out view)) return false;
                _lastClient = client;
                _lastView = view;
            }
            if (!view.Known) return false;
            var replicable = owner.GetParent() ?? owner;
            var interval = LayerSendInterval(client, replicable);
            if (interval < 0) return false;
            var frames = replicable == view.Controlled || replicable == view.Character ? 1
                : entry.Group.IsHighPriority ? Math.Max(interval >> 4, 1) : interval;
            var due = MyRandom.Instance.Next(1, frames * 2) + currentTime;
            var queue = DirtyQueue(client);
            var valid = owner.IsValid || Postponed(server, owner);
            if (!queue.Contains(entry))
            {
                if (valid) queue.Enqueue(entry, due);
            }
            else if (valid)
            {
                if (due < EntryPriority(entry)) queue.UpdatePriority(entry, due);
            }
            else queue.Remove(entry);
            return true;
        }

        /// <summary>The parked entries due by this frame put in the client's queue by the game's scheduling.</summary>
        private static void ReleaseDue(MyReplicationServer server, object client, ClientView view, long frame)
        {
            var second = frame / FramesPerSlot;
            if (view.NextSlot == 0) view.NextSlot = second;
            for (; view.NextSlot <= second; view.NextSlot++)
            {
                if (!view.Slots.TryGetValue(view.NextSlot, out var slot)) continue;
                view.Slots.Remove(view.NextSlot);
                foreach (var entry in slot)
                {
                    // (parked once more or taken out since: its own slot is another, or none)
                    if (!view.Parked.TryGetValue(entry, out var due) || due / FramesPerSlot != view.NextSlot) continue;
                    view.Parked.Remove(entry);
                    if (!Current(client, entry)) continue;
                    _rescheduling = true;
                    try
                    {
                        ScheduleSync(server, client, entry, frame, true);
                    }
                    finally
                    {
                        _rescheduling = false;
                    }
                }
                slot.Clear();
                view.Spare.Push(slot);
            }
        }

        // (the client still has this group, through this very entry: a replicable that left and came back has a new one)
        private static bool Current(object client, MyStateDataEntry entry) =>
            StateGroups(client).TryGetValue(entry.Group, out var current) && ReferenceEquals(current, entry);

        /// <summary>Once a frame: whoever just opened something gets their inventories at once.</summary>
        private static void SendUpdatePrefix(MyReplicationServer __instance)
        {
            _server = __instance;
            var clients = (System.Collections.IDictionary)ClientStatesField.GetValue(__instance);
            if (clients == null) return;
            var frame = SyncFrame(__instance);
            foreach (var client in clients.Values)
            {
                var view = Views.GetOrCreateValue(client);
                var state = ClientState(client) as MyClientState;
                var looking = state?.ContextEntity != null;
                if (looking && !view.Looking) WakeInventories(client, view, frame);
                else if (view.Parked.Count > 0) ReleaseDue(__instance, client, view, frame);
                view.Looking = looking;
                // Each of these looks the player up, and the replicable after it, in dictionaries; they
                // are read here once a frame instead of on every inventory scheduled for the client.
                view.Controlled = state?.ControlledReplicable;
                view.Character = state?.CharacterReplicable;
                view.Position = state?.Position;
                view.Known = state != null;
            }
        }

        private static void WakeInventories(object client, ClientView view, long frame)
        {
            var queue = DirtyQueue(client);
            foreach (var entry in view.Parked.Keys)
            {
                // (counted below with the queued ones)
                if (!Current(client, entry) || queue.Contains(entry)) continue;
                queue.Enqueue(entry, frame);
            }
            view.Unpark();
            foreach (var pair in StateGroups(client))
            {
                if (pair.Key == null || pair.Key.GetType() != InventoryGroupType) continue;
                if (!queue.Contains(pair.Value)) continue;
                queue.UpdatePriority(pair.Value, frame);
                WokenUp++;
            }
        }

        /// <summary>
        /// Reads and resets the wake counter so a test can watch a single open: the counter is the
        /// race-free proof that opening pulled the queue forward, unlike a queue snapshot a fast
        /// server may already have sent through.
        /// </summary>
        public static long TakeWokenUp()
        {
            var value = WokenUp;
            WokenUp = 0;
            return value;
        }

        private static readonly HashSet<MyStateDataEntry> Suspects = new HashSet<MyStateDataEntry>();

        /// <summary>
        /// For a test: every state group a client still has something to be sent of (<c>IsStillDirty</c>; streams aside)
        /// must wait for it - in the client's queue, or parked here; one that waits nowhere is never sent. A group can be
        /// dirty and not queued for a moment (an ack lost, the group back in the server's dirty groups till the next
        /// pass), so a group counts as lost only when it was so at the previous call too. Returns the groups looked at,
        /// dirty, queued, parked, lost, and the first lost one's type.
        /// </summary>
        public static string VerifyQueues(out long lost)
        {
            lost = 0;
            if (_server == null) return "no server seen";
            long groups = 0, dirty = 0, queued = 0, parked = 0, overdue = 0;
            string first = null;
            var frame = SyncFrame(_server);
            var now = new HashSet<MyStateDataEntry>();
            var clients = (System.Collections.IDictionary)ClientStatesField.GetValue(_server);
            foreach (var client in clients.Values)
            {
                var endpoint = ClientState(client).EndpointId;
                var queue = DirtyQueue(client);
                Views.TryGetValue(client, out var view);
                foreach (var pair in StateGroups(client))
                {
                    var group = pair.Key;
                    if (group == null || group.IsStreaming) continue;
                    groups++;
                    bool still;
                    try { still = group.IsStillDirty(endpoint); }
                    catch { continue; }
                    if (!still) continue;
                    dirty++;
                    if (queue.Contains(pair.Value)) { queued++; continue; }
                    if (view != null && view.Parked.TryGetValue(pair.Value, out var due))
                    {
                        parked++;
                        // (parked past its second and not put back: as good as lost)
                        if (due + 3 * FramesPerSlot >= frame) continue;
                        overdue++;
                        lost++;
                        if (first == null) first = "overdue " + group.GetType().Name + " of " + pair.Value.Owner?.GetType().Name;
                        continue;
                    }
                    now.Add(pair.Value);
                    if (!Suspects.Contains(pair.Value)) continue;
                    lost++;
                    if (first == null) first = group.GetType().Name + " of " + pair.Value.Owner?.GetType().Name;
                }
            }
            Suspects.Clear();
            Suspects.UnionWith(now);
            return $"groups={groups} dirty={dirty} queued={queued} parked={parked} (overdue {overdue}) not waiting={now.Count} lost={lost}" + (first != null ? $" (first: {first})" : "");
        }

        /// <summary>How many inventories of this client are queued, and how many are due within so many frames.</summary>
        public static long[] InventoryQueueState(MyClientStateBase state, long withinFrames)
        {
            if (_server == null) return new long[2];
            var clients = (System.Collections.IDictionary)ClientStatesField.GetValue(_server);
            var frame = SyncFrame(_server);
            foreach (var client in clients.Values)
            {
                if (!ReferenceEquals(ClientState(client), state)) continue;
                var queue = DirtyQueue(client);
                long queued = 0, due = 0;
                if (Views.TryGetValue(client, out var view))
                    foreach (var parked in view.Parked)
                    {
                        queued++;
                        if (parked.Value - frame <= withinFrames) due++;
                    }
                foreach (var pair in StateGroups(client))
                {
                    if (pair.Key == null || pair.Key.GetType() != InventoryGroupType) continue;
                    if (!queue.Contains(pair.Value)) continue;
                    queued++;
                    if (EntryPriority(pair.Value) - frame <= withinFrames) due++;
                }
                return new[] { queued, due };
            }
            return new long[2];
        }

        private static bool ShouldDelay(object client, MyStateDataEntry groupEntry)
        {
            if (groupEntry?.Group == null || groupEntry.Group.GetType() != InventoryGroupType) return false;
            // A client not seen by this frame's update yet is served the normal way.
            if (!Views.TryGetValue(client, out var view) || !view.Known) return false;
            if (!(ClientState(client) is MyClientState state)) return false;
            // Something is open in front of the client: it may well be this inventory.
            if (state.ContextEntity != null) return false;
            var owner = groupEntry.Owner;
            if (owner == null) return false;
            // The client's own character and whatever it is sitting in are never held back.
            if (owner == view.Controlled || owner == view.Character) return false;
            var parent = owner.GetParent();
            if (parent != null && (parent == view.Controlled || parent == view.Character)) return false;
            // Neither is whatever is right around the client - the subgrids of its own ship, what it is
            // docked to, the base it stands in - which its HUD may show without anything being open.
            var position = view.Position;
            if (position.HasValue && (parent ?? owner).GetAABB().DistanceSquared(position.Value) <= NearDistance * NearDistance)
                return false;
            // Only when the layer is already known, so no decision about removing the replicable is skipped.
            return ((System.Collections.IDictionary)ReplicableToLayer(client)).Contains(parent ?? owner);
        }

        private static Func<object, T> BuildClientField<T>(string name)
        {
            var client = Expression.Parameter(typeof(object), "client");
            var body = Expression.Convert(Expression.Field(Expression.Convert(client, ClientType), name), typeof(T));
            return Expression.Lambda<Func<object, T>>(body, client).Compile();
        }

        private static Action<MyReplicationServer, object, MyStateDataEntry, long, bool> BuildScheduleSync()
        {
            var method = ServerType.GetMethod("ScheduleStateGroupSync", BindingFlags.Instance | BindingFlags.NonPublic);
            var server = Expression.Parameter(typeof(MyReplicationServer), "server");
            var client = Expression.Parameter(typeof(object), "client");
            var entry = Expression.Parameter(typeof(MyStateDataEntry), "entry");
            var time = Expression.Parameter(typeof(long), "time");
            var allowRemoval = Expression.Parameter(typeof(bool), "allowRemoval");
            var call = Expression.Call(server, method, Expression.Convert(client, ClientType), entry, time, allowRemoval);
            return Expression.Lambda<Action<MyReplicationServer, object, MyStateDataEntry, long, bool>>(
                call, server, client, entry, time, allowRemoval).Compile();
        }

        private static Func<MyStateDataEntry, long> BuildEntryPriority()
        {
            var field = typeof(MyStateDataEntry).GetField("Priority", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? typeof(FastPriorityQueue<MyStateDataEntry>.Node).GetField("Priority", BindingFlags.Instance | BindingFlags.NonPublic);
            var entry = Expression.Parameter(typeof(MyStateDataEntry), "entry");
            return Expression.Lambda<Func<MyStateDataEntry, long>>(Expression.Field(entry, field), entry).Compile();
        }

        // (client.ReplicableToLayer[replicable].Descriptor.SendInterval, or -1 when the client has no layer for it)
        private static Func<object, IMyReplicable, int> BuildLayerSendInterval()
        {
            var client = Expression.Parameter(typeof(object), "client");
            var replicable = Expression.Parameter(typeof(IMyReplicable), "replicable");
            var map = Expression.Field(Expression.Convert(client, ClientType), "ReplicableToLayer");
            var layerType = map.Type.GetGenericArguments()[1];
            var layer = Expression.Variable(layerType, "layer");
            var found = Expression.Call(map, map.Type.GetMethod("TryGetValue"), replicable, layer);
            var interval = Expression.Field(Expression.Field(layer, "Descriptor"), "SendInterval");
            var body = Expression.Block(new[] { layer }, Expression.Condition(found, interval, Expression.Constant(-1)));
            return Expression.Lambda<Func<object, IMyReplicable, int>>(body, client, replicable).Compile();
        }

        private static Func<MyReplicationServer, IMyReplicable, bool> BuildPostponed()
        {
            var server = Expression.Parameter(typeof(MyReplicationServer), "server");
            var replicable = Expression.Parameter(typeof(IMyReplicable), "replicable");
            var set = Expression.Field(server, ServerType.GetField("m_postponedDestructionReplicables", BindingFlags.Instance | BindingFlags.NonPublic));
            var body = Expression.Call(set, set.Type.GetMethod("Contains"), replicable);
            return Expression.Lambda<Func<MyReplicationServer, IMyReplicable, bool>>(body, server, replicable).Compile();
        }

        private static Func<MyReplicationServer, long> BuildSyncFrame()
        {
            var field = typeof(MyReplicationLayer).GetField("SyncFrameCounter", BindingFlags.Instance | BindingFlags.NonPublic);
            var server = Expression.Parameter(typeof(MyReplicationServer), "server");
            return Expression.Lambda<Func<MyReplicationServer, long>>(Expression.Field(server, field), server).Compile();
        }

        /// <summary>A parked entry waits in the list of its second.</summary>
        private const long FramesPerSlot = 60;

        private sealed class ClientView
        {
            /// <summary>Parked entries and the frame each is due at.</summary>
            public readonly Dictionary<MyStateDataEntry, long> Parked = new Dictionary<MyStateDataEntry, long>();
            /// <summary>Parked entries by the second they are due in; one parked again stays in its old list too, the frame in <see cref="Parked"/> decides.</summary>
            public readonly Dictionary<long, List<MyStateDataEntry>> Slots = new Dictionary<long, List<MyStateDataEntry>>();
            public readonly Stack<List<MyStateDataEntry>> Spare = new Stack<List<MyStateDataEntry>>();
            public long NextSlot;

            public void Park(MyStateDataEntry entry, long due)
            {
                // (due in a second already gone through - this one, with a delay of a few frames: the next second's list,
                // or nothing would take it out)
                if (NextSlot != 0 && due / FramesPerSlot < NextSlot) due = NextSlot * FramesPerSlot;
                Parked[entry] = due;
                var second = due / FramesPerSlot;
                if (!Slots.TryGetValue(second, out var slot))
                    Slots[second] = slot = Spare.Count > 0 ? Spare.Pop() : new List<MyStateDataEntry>();
                slot.Add(entry);
            }

            public void Unpark()
            {
                Parked.Clear();
                foreach (var slot in Slots.Values)
                {
                    slot.Clear();
                    Spare.Push(slot);
                }
                Slots.Clear();
            }

            public bool Known;
            public bool Looking;
            public IMyReplicable Controlled;
            public IMyReplicable Character;
            public VRageMath.Vector3D? Position;
        }
    }
}
