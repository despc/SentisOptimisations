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
    /// A client with nothing open in front of it (no context entity) gets those inventories every
    /// <see cref="IdleSendIntervalFrames"/> frames instead of every few. Nothing is dropped: the delta
    /// is computed against what that client last received, so the next update carries everything that
    /// changed meanwhile. The moment the client opens something, all of its inventories are pulled to
    /// the front of its queue and it is served at the normal rate again, so opening a terminal shows
    /// the contents without the idle wait. The client's own character and the entity it controls are
    /// never held back, and neither is anything but inventories.
    /// </summary>
    [PatchShim]
    public static class IdleInventorySync
    {
        /// <summary>How rarely, on average, an inventory goes to a client that has nothing open, in frames.</summary>
        public const long IdleSendIntervalFrames = 600;

        public static long Delayed;
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

        /// <summary>Whether a client had something open when it was last looked at; drops with the client.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Flag> Looking =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, Flag>();
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
                var looking = IsLooking(client);
                var flag = Looking.GetOrCreateValue(client);
                if (looking && !flag.Looking) WakeInventories(client, frame);
                flag.Looking = looking;
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

        private static bool IsLooking(object client) =>
            ClientState(client) is MyClientState state && state.ContextEntity != null;

        private static bool ShouldDelay(object client, MyStateDataEntry groupEntry)
        {
            if (groupEntry?.Group == null || groupEntry.Group.GetType() != InventoryGroupType) return false;
            if (!(ClientState(client) is MyClientState state)) return false;
            // Something is open in front of the client: it may well be this inventory.
            if (state.ContextEntity != null) return false;
            var owner = groupEntry.Owner;
            if (owner == null) return false;
            // The client's own character and whatever it is sitting in are never held back.
            if (owner == state.ControlledReplicable || owner == state.CharacterReplicable) return false;
            var parent = owner.GetParent();
            if (parent != null && (parent == state.ControlledReplicable || parent == state.CharacterReplicable)) return false;
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

        private sealed class Flag
        {
            public bool Looking;
        }
    }
}
