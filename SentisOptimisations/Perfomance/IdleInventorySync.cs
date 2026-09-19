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
            if (_rescheduling || !ShouldDelay(client, groupEntry))
            {
                Normal++;
                return true;
            }
            // Already waiting for its turn: leave it there. The game moves a queued entry to whichever
            // time is sooner, and every change of a busy inventory schedules it again, so with a new
            // random delay each time the soonest of dozens of draws won and the idle interval was
            // never reached (measured: 768 thousand inventory writes a minute at 1800 frames, the
            // same as at 600).
            if (DirtyQueue(client).Contains(groupEntry))
            {
                Kept++;
                return false;
            }
            // The game's own scheduling, only from a later frame, so the layer's interval, its random
            // spread and the removal of replicables that left the layer all still apply.
            Delayed++;
            _rescheduling = true;
            try
            {
                // Spread over the whole idle interval: shifting every inventory by the same amount only
                // moves the pile-up, and a frame that serves a client's 400 inventories at once is
                // exactly what this is meant to prevent.
                var delay = MyRandom.Instance.Next(1, (int)(IdleSendIntervalFrames * 2));
                ScheduleSync(__instance, client, groupEntry, currentTime + delay, allowReplicableRemoval);
            }
            finally
            {
                _rescheduling = false;
            }
            return false;
        }

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
                if (looking && !view.Looking) WakeInventories(client, frame);
                view.Looking = looking;
                // Each of these looks the player up, and the replicable after it, in dictionaries; they
                // are read here once a frame instead of on every inventory scheduled for the client.
                view.Controlled = state?.ControlledReplicable;
                view.Character = state?.CharacterReplicable;
                view.Position = state?.Position;
                view.Known = state != null;
            }
        }

        private static void WakeInventories(object client, long frame)
        {
            var queue = DirtyQueue(client);
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

        private static Func<MyReplicationServer, long> BuildSyncFrame()
        {
            var field = typeof(MyReplicationLayer).GetField("SyncFrameCounter", BindingFlags.Instance | BindingFlags.NonPublic);
            var server = Expression.Parameter(typeof(MyReplicationServer), "server");
            return Expression.Lambda<Func<MyReplicationServer, long>>(Expression.Field(server, field), server).Compile();
        }

        private sealed class ClientView
        {
            public bool Known;
            public bool Looking;
            public IMyReplicable Controlled;
            public IMyReplicable Character;
            public VRageMath.Vector3D? Position;
        }
    }
}
