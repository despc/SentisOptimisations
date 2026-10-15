using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using VRage.Collections;
using VRage.Network;
using VRage.Replication;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// An asteroid, a planet or a grid a client was sent and never took is sent to it again.
    ///
    /// Players see some asteroids and grids never appear. A voxel map or a grid comes to the client streamed, in parts;
    /// once it has them all the client loads it and answers "ready" (<c>MyReplicationClient.SetReplicableReady</c>). A
    /// load that fails - the client's own copy of a voxel storage gone ("Failed to load voxel from cache."), an asteroid
    /// sent by its id the client does not have, a broken stream (<c>LoadCancel</c>) - is answered with nothing: the client
    /// drops the replicable and the server keeps it pending for that client (<c>IsPending</c>) for as long as it stays in
    /// range. Nothing sends it again; it is there only after the player flies away and comes back. The same when:
    ///  * the server never finishes making the stream (<c>Serialize</c> of a voxel map or grid closed meanwhile:
    ///    <c>Processing</c> for good, and the client's other streams behind it, one at a time);
    ///  * a grid split off is sent not streamed but by its id, the client to take the grid its own split makes
    ///    (<c>MyCubeGridReplicable.OnLoad</c> waits for <c>OnSplitGridCreated</c>): a client whose split came out otherwise
    ///    waits for good.
    ///
    /// Here a client with anything pending (<c>MyClient.PendingReplicables</c>; nothing in the steady state, and then no
    /// client is looked at) is looked at once in <see cref="EverySeconds"/> s, one client a frame: its table is tens of
    /// thousands of replicables (all 64 clients at once cost a 130 ms frame). A voxel map or grid still pending for a
    /// client - after the client got the whole stream and acknowledged it (<see cref="DeliveredSeconds"/> s for an
    /// asteroid, <see cref="DeliveredGridSeconds"/> for a grid, <see cref="DeliveredPlanetSeconds"/> for a planet: their
    /// loads take the client longer), after it was sent whole without a stream (<see cref="CreatedSeconds"/>), or with its
    /// stream in the making for <see cref="MakingSeconds"/> s - is taken from that client and given anew, as the game does
    /// when it comes into range (<c>RemoveForClient</c> with the destroy sent, <c>AddForClient</c>): the stream is made
    /// again, with the data if the client said its voxel cache failed. A stream waiting its turn or on its way is left
    /// alone. One the client keeps not taking waits twice as long before each next resend (up to
    /// <see cref="MaxDoublings"/> doublings), until it is taken or leaves the client's range. The resends are logged, so a
    /// client that keeps failing shows (asteroid_resend, grid_resend).
    /// </summary>
    public static class ReplicableResend
    {
        public const int EverySeconds = 2;
        public const int DeliveredSeconds = 30;
        public const int DeliveredGridSeconds = 60;
        public const int DeliveredPlanetSeconds = 120;
        public const int CreatedSeconds = 30;
        public const int MakingSeconds = 120;
        /// <summary>A replicable a client keeps not taking waits twice as long before each next resend, up to 2^this.</summary>
        public const int MaxDoublings = 4;

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Replicables sent to a client again since the start.</summary>
        public static long Resent;

        private static readonly Type VoxelReplicableType = typeof(MyMultiplayer).Assembly.GetType("Sandbox.Game.Replication.MyVoxelReplicable");
        private static readonly Type GridReplicableType = typeof(MyMultiplayer).Assembly.GetType("Sandbox.Game.Replication.MyCubeGridReplicable");
        private static FieldInfo _clientStates, _clientReplicables, _clientPending;
        private static MethodInfo _removeForClient, _addForClient;
        private static PropertyInfo _voxelInstance;
        private static bool _broken;

        private enum Wait { None, Delivered, Created, Making }

        private sealed class Watch
        {
            public Wait Kind;
            public ulong Since;
            public ulong SeenAt;
            /// <summary>How many times it has been sent again to this client already: each next wait is twice as long.</summary>
            public int Resends;
        }

        // (client, replicable) -> since when it has waited the way it does
        private static readonly Dictionary<(Endpoint, IMyReplicable), Watch> Watches = new Dictionary<(Endpoint, IMyReplicable), Watch>();
        private static readonly List<(Endpoint, IMyReplicable)> Gone = new List<(Endpoint, IMyReplicable)>();
        private static readonly List<IMyStateGroup> Groups = new List<IMyStateGroup>();
        private static readonly List<(Endpoint Endpoint, IMyReplicable Replicable, string Why)> Due = new List<(Endpoint, IMyReplicable, string)>();

        /// <summary>When each client was looked at last.</summary>
        private static readonly Dictionary<Endpoint, ulong> LookedAt = new Dictionary<Endpoint, ulong>();
        private static readonly List<Endpoint> Left = new List<Endpoint>();

        /// <summary>Every frame, game thread: at most one client looked at.</summary>
        public static void Tick()
        {
            if (_broken) return;
            var server = MyMultiplayer.Static?.ReplicationLayer as MyReplicationServer;
            if (server == null || VoxelReplicableType == null || GridReplicableType == null) return;
            var frame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            try
            {
                Bind();
                var states = (IDictionary)_clientStates.GetValue(server);
                if (frame % 600 == 0) Forget(states);
                // (of the clients with something pending not looked at in EverySeconds, the one looked at longest ago)
                object due = null;
                var dueEndpoint = default(Endpoint);
                var dueAt = ulong.MaxValue;
                foreach (DictionaryEntry entry in states)
                {
                    if ((int)_clientPending.GetValue(entry.Value) <= 0) continue;
                    var endpoint = (Endpoint)entry.Key;
                    LookedAt.TryGetValue(endpoint, out var at);
                    if (at != 0 && frame - at < (ulong)(EverySeconds * 60) || at >= dueAt) continue;
                    due = entry.Value;
                    dueEndpoint = endpoint;
                    dueAt = at;
                }
                if (due == null) return;
                LookedAt[dueEndpoint] = frame;
                Look(server, due, dueEndpoint, frame);
            }
            catch (Exception e)
            {
                _broken = true;
                SentisOptimisationsPlugin.Log.Error(e, "ReplicableResend: off");
            }
        }

        private static void Bind()
        {
            if (_clientStates != null) return;
            var clientType = typeof(MyReplicationServer).Assembly.GetType("VRage.Network.MyClient") ?? throw new TypeLoadException("VRage.Network.MyClient");
            _clientReplicables = clientType.GetField("Replicables", Any) ?? throw new MissingFieldException("MyClient.Replicables");
            _clientPending = clientType.GetField("PendingReplicables", Any) ?? throw new MissingFieldException("MyClient.PendingReplicables");
            _removeForClient = typeof(MyReplicationServer).GetMethod("RemoveForClient", Any, null, new[] { typeof(IMyReplicable), clientType, typeof(bool) }, null)
                               ?? throw new MissingMethodException("MyReplicationServer.RemoveForClient");
            _addForClient = typeof(MyReplicationServer).GetMethod("AddForClient", Any, null, new[] { typeof(IMyReplicable), typeof(Endpoint), clientType, typeof(bool), typeof(bool) }, null)
                            ?? throw new MissingMethodException("MyReplicationServer.AddForClient");
            _voxelInstance = VoxelReplicableType.GetProperty("Instance", Any) ?? throw new MissingMemberException("MyVoxelReplicable.Instance");
            _clientStates = typeof(MyReplicationServer).GetField("m_clientStates", Any) ?? throw new MissingFieldException("MyReplicationServer.m_clientStates");
        }

        // (the clients gone: their marks and watches dropped)
        private static void Forget(IDictionary states)
        {
            foreach (var endpoint in LookedAt.Keys)
                if (!states.Contains(endpoint)) Left.Add(endpoint);
            foreach (var endpoint in Left) LookedAt.Remove(endpoint);
            foreach (var watch in Watches)
                if (!states.Contains(watch.Key.Item1)) Gone.Add(watch.Key);
            foreach (var key in Gone) Watches.Remove(key);
            Left.Clear();
            Gone.Clear();
        }

        private static void Look(MyReplicationServer server, object client, Endpoint endpoint, ulong frame)
        {
            var replicables = (MyConcurrentDictionary<IMyReplicable, MyReplicableClientData>)_clientReplicables.GetValue(client);
            foreach (var pair in replicables)
            {
                if (!pair.Value.IsPending) continue;
                var replicable = pair.Key;
                var type = replicable.GetType();
                var voxel = type == VoxelReplicableType;
                if (!voxel && type != GridReplicableType) continue;
                var wait = Waiting(replicable, pair.Value, endpoint);
                var key = (endpoint, replicable);
                if (wait == Wait.None)
                {
                    // (on its way: nothing to time; a resend's count is kept while it stays pending)
                    if (Watches.TryGetValue(key, out var on))
                    {
                        if (on.Resends == 0) Watches.Remove(key);
                        else { on.Kind = Wait.None; on.SeenAt = frame; }
                    }
                    continue;
                }
                if (!Watches.TryGetValue(key, out var watch))
                    Watches[key] = watch = new Watch { Kind = wait, Since = frame };
                else if (watch.Kind != wait)
                {
                    watch.Kind = wait;
                    watch.Since = frame;
                }
                watch.SeenAt = frame;
                var limit = wait == Wait.Making ? MakingSeconds
                    : wait == Wait.Created ? CreatedSeconds
                    : !voxel ? DeliveredGridSeconds
                    : _voxelInstance.GetValue(replicable) is MyPlanet ? DeliveredPlanetSeconds : DeliveredSeconds;
                limit <<= Math.Min(watch.Resends, MaxDoublings);
                var waited = (frame - watch.Since) / 60.0;
                if (waited >= limit)
                    Due.Add((endpoint, replicable, $"{(watch.Resends > 0 ? $"again ({watch.Resends + 1}), " : "")}" +
                                                   $"{(wait == Wait.Making ? "its stream in the making" : wait == Wait.Created ? "sent whole, not taken" : "streamed whole, not taken")} for {waited:0} s"));
            }
            // (this client's watches not seen pending now: taken, or out of its range)
            foreach (var watch in Watches)
                if (watch.Key.Item1.Equals(endpoint) && watch.Value.SeenAt != frame) Gone.Add(watch.Key);
            foreach (var key in Gone) Watches.Remove(key);
            Gone.Clear();

            foreach (var due in Due)
            {
                var resent = Watches[(due.Endpoint, due.Replicable)];
                resent.Resends++;
                resent.Kind = Wait.None;
                resent.Since = frame;
                _removeForClient.Invoke(server, new[] { due.Replicable, client, (object)true });
                _addForClient.Invoke(server, new[] { due.Replicable, due.Endpoint, client, (object)false, (object)false });
                Resent++;
                SentisOptimisationsPlugin.Log.Warn($"ReplicableResend: {due.Replicable.GetType().Name} '{due.Replicable.InstanceName}' sent again to client {due.Endpoint.Id.Value} ({due.Why}); {Resent} since the start");
            }
            Due.Clear();
        }

        // (how a pending replicable waits for this client: its whole stream delivered, sent whole without a stream, its
        // stream being made, or neither - not started, on its way)
        private static Wait Waiting(IMyReplicable replicable, MyReplicableClientData data, Endpoint endpoint)
        {
            if (!data.IsStreaming) return Wait.Created;
            Groups.Clear();
            replicable.GetStateGroups(Groups);
            try
            {
                foreach (var group in Groups)
                {
                    if (!group.IsStreaming) continue;
                    if (group is IMyStreamingStateGroup streaming && streaming.HasStreamed(endpoint)) return Wait.Delivered;
                    if (group.IsProcessingForClient(endpoint) == MyStreamProcessingState.Processing) return Wait.Making;
                    return Wait.None;
                }
                return Wait.None;
            }
            finally
            {
                Groups.Clear();
            }
        }
    }
}
