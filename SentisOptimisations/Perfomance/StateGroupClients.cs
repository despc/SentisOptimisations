using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Torch.Managers.PatchManager;
using VRage.Network;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Applies dirty state groups through an index of the clients that actually have them.
    ///
    /// Vanilla's MyReplicationServer.ApplyDirtyGroups takes every queued group and looks it up in
    /// every client's state groups, so the cost is groups times clients. A group is queued on every
    /// property change and on every lost packet reported by any client, so on a busy base with many
    /// players this becomes tens of thousands of lookups per frame: a single frame of the replication
    /// test spent 70 ms there with 64 clients.
    ///
    /// Here every group keeps the list of clients it is replicated to, filled where vanilla adds and
    /// removes a client's state groups, and the queue is applied through that list. Queuing the same
    /// group twice in one frame changes nothing, so duplicates are dropped as well.
    ///
    /// <see cref="VerifyIndex"/> compares the index with the vanilla scan over all clients and counts
    /// the differences; the replication test turns it on for one window.
    /// </summary>
    [PatchShim]
    public static class StateGroupClients
    {
        private struct Holder
        {
            public object Client;
            public MyStateDataEntry Entry;
        }

        private static readonly Dictionary<IMyStateGroup, List<Holder>> Index =
            new Dictionary<IMyStateGroup, List<Holder>>(InstanceComparer.Instance);
        private static readonly List<IMyStateGroup> Order = new List<IMyStateGroup>();
        private static readonly HashSet<IMyStateGroup> Unique = new HashSet<IMyStateGroup>(InstanceComparer.Instance);

        public static bool VerifyIndex;
        public static long VerifyMismatches;
        public static string VerifyFirstDifference;
        public static long Queued;
        public static long Applied;
        public static long Scheduled;

        private static readonly Type ServerType = typeof(MyReplicationServer);
        private static readonly Type ClientType = ServerType.Assembly.GetType("VRage.Network.MyClient", true);
        private static readonly FieldInfo DirtyGroupsField = ServerType.GetField("m_dirtyGroups", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ReplicableGroupsField = ServerType.GetField("m_replicableGroups", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ClientStatesField = ServerType.GetField("m_clientStates", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Func<object, Dictionary<IMyStateGroup, MyStateDataEntry>> ClientStateGroups = BuildClientStateGroups();
        private static readonly Action<MyReplicationServer, object, MyStateDataEntry, long, bool> ScheduleSync = BuildScheduleSync();
        private static readonly Func<MyReplicationServer, long> SyncFrame = BuildSyncFrame();

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("StateGroupClients", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
            const BindingFlags anyStatic = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            var self = typeof(StateGroupClients);
            ctx.GetPattern(ServerType.GetMethod("AddClientReplicable", instance))
                .Suffixes.Add(self.GetMethod(nameof(AddClientReplicableSuffix), anyStatic));
            ctx.GetPattern(ServerType.GetMethod("RemoveClientReplicable", instance))
                .Suffixes.Add(self.GetMethod(nameof(RemoveClientReplicableSuffix), anyStatic));
            ctx.GetPattern(ServerType.GetMethod("ApplyDirtyGroups", instance))
                .Prefixes.Add(self.GetMethod(nameof(ApplyDirtyGroupsPrefix), anyStatic));
        }

        private static void AddClientReplicableSuffix(MyReplicationServer __instance, IMyReplicable replicable, object client)
        {
            var groups = GroupsOf(__instance, replicable);
            if (groups == null) return;
            var stateGroups = ClientStateGroups(client);
            foreach (var group in groups)
            {
                if (!stateGroups.TryGetValue(group, out var entry)) continue;
                if (!Index.TryGetValue(group, out var holders)) Index[group] = holders = new List<Holder>();
                var known = false;
                for (var i = 0; i < holders.Count; i++)
                {
                    if (!ReferenceEquals(holders[i].Client, client)) continue;
                    holders[i] = new Holder { Client = client, Entry = entry };
                    known = true;
                    break;
                }
                if (!known) holders.Add(new Holder { Client = client, Entry = entry });
            }
        }

        private static void RemoveClientReplicableSuffix(MyReplicationServer __instance, IMyReplicable replicable, object client)
        {
            var groups = GroupsOf(__instance, replicable);
            if (groups == null) return;
            foreach (var group in groups)
            {
                if (!Index.TryGetValue(group, out var holders)) continue;
                for (var i = holders.Count - 1; i >= 0; i--)
                    if (ReferenceEquals(holders[i].Client, client)) holders.RemoveAt(i);
                if (holders.Count == 0) Index.Remove(group);
            }
        }

        private static bool ApplyDirtyGroupsPrefix(MyReplicationServer __instance)
        {
            var queue = (ConcurrentQueue<IMyStateGroup>)DirtyGroupsField.GetValue(__instance);
            if (queue.IsEmpty) return false;
            var frame = SyncFrame(__instance);
            try
            {
                // Only what was queued when this started; anything queued meanwhile waits for the next call.
                var count = queue.Count;
                while (count-- > 0 && queue.TryDequeue(out var group))
                {
                    Queued++;
                    if (Unique.Add(group)) Order.Add(group);
                }
                foreach (var group in Order)
                {
                    Applied++;
                    if (VerifyIndex) Verify(__instance, group);
                    if (!Index.TryGetValue(group, out var holders)) continue;
                    for (var i = 0; i < holders.Count; i++)
                    {
                        ScheduleSync(__instance, holders[i].Client, holders[i].Entry, frame, true);
                        Scheduled++;
                    }
                }
            }
            finally
            {
                Unique.Clear();
                Order.Clear();
            }
            return false;
        }

        /// <summary>Compares the index with the vanilla scan over every client.</summary>
        private static void Verify(MyReplicationServer server, IMyStateGroup group)
        {
            var clients = (System.Collections.IDictionary)ClientStatesField.GetValue(server);
            var expected = 0;
            foreach (var client in clients.Values)
                if (ClientStateGroups(client).ContainsKey(group)) expected++;
            var actual = Index.TryGetValue(group, out var holders) ? holders.Count : 0;
            if (expected == actual) return;
            VerifyMismatches++;
            if (VerifyFirstDifference == null)
                VerifyFirstDifference = group.GetType().Name + ": index has " + actual + " clients, they have it " + expected + " times";
        }

        private static List<IMyStateGroup> GroupsOf(MyReplicationServer server, IMyReplicable replicable)
        {
            var groups = (Dictionary<IMyReplicable, List<IMyStateGroup>>)ReplicableGroupsField.GetValue(server);
            return groups.TryGetValue(replicable, out var list) ? list : null;
        }

        private static Func<object, Dictionary<IMyStateGroup, MyStateDataEntry>> BuildClientStateGroups()
        {
            var client = Expression.Parameter(typeof(object), "client");
            var body = Expression.Field(Expression.Convert(client, ClientType), "StateGroups");
            return Expression.Lambda<Func<object, Dictionary<IMyStateGroup, MyStateDataEntry>>>(body, client).Compile();
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

        private static Func<MyReplicationServer, long> BuildSyncFrame()
        {
            var field = typeof(MyReplicationLayer).GetField("SyncFrameCounter", BindingFlags.Instance | BindingFlags.NonPublic);
            var server = Expression.Parameter(typeof(MyReplicationServer), "server");
            return Expression.Lambda<Func<MyReplicationServer, long>>(Expression.Field(server, field), server).Compile();
        }

        private sealed class InstanceComparer : IEqualityComparer<IMyStateGroup>
        {
            public static readonly InstanceComparer Instance = new InstanceComparer();
            public bool Equals(IMyStateGroup x, IMyStateGroup y) => ReferenceEquals(x, y);
            public int GetHashCode(IMyStateGroup obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
