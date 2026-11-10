using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Sandbox.Game.Replication;
using Torch.Managers.PatchManager;
using VRage.Collections;
using VRage.Network;
using VRage.Replication;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// The grids a client is streamed: nearest to its player first, and no more than a few loading on the client at once.
    ///
    /// Players freeze in big fights as structures load. A grid comes to a client streamed: the server makes the stream,
    /// sends it, and the client builds the grid (<c>MyCubeGridReplicable.OnLoad</c>: the grid made off the game thread,
    /// put into the scene on it) and answers "ready" (<c>MyReplicationServer.ReplicableReady</c>). The server starts the
    /// next stream as soon as the last one is sent, not when the client has built it, and in the order of chance within a
    /// layer of distance (<c>ScheduleStateGroupSync</c>: a random delay of up to two send intervals). Many grids coming
    /// into range at once came one after another as fast as the network took them, were built together and put into the
    /// client's scene together: one long freeze, the far ones as likely first as the near.
    ///
    /// Here, as <c>VoxelStreamNearestFirst</c> does for voxel maps:
    ///  * a grid's stream is put in the client's queue by its distance from the client's player, one frame later for
    ///    every <see cref="MetresPerFrame"/> m (zero inside its box; a held stream is put off by as much again on top
    ///    of its wait, so the nearer still go first);
    ///  * a new grid stream to a client is not started while <see cref="InFlight"/> grids it was streamed are not
    ///    ready on it yet (<c>MyReplicableClientData.IsPending</c>): the client builds them one or two at a time, with its
    ///    own frames between. A grid not ready in <see cref="InFlightTimeoutSeconds"/> s no longer holds the others back
    ///    (<c>ReplicableResend</c> sees to one never taken). The grid the client controls is never held back.
    /// Voxel maps and the other state groups are not held back: a held grid stream gives way to them in the queue.
    ///
    /// What a client was sent is counted (grids streamed, how long they took to be ready, how often one waited; what was
    /// created on it without a stream, by type), in the stream logs once a minute (off by default) and for SentisTests.
    /// </summary>
    [PatchShim]
    public static class GridStreamPacing
    {
        /// <summary>Distance that puts a grid's stream one frame later in the queue.</summary>
        public const double MetresPerFrame = 50;
        /// <summary>Frames a held grid stream is put back by before it is asked about again.</summary>
        public const int RetryFrames = 6;
        /// <summary>The most a grid's stream is put later by for its distance (2 s: 6 km and farther alike).</summary>
        public const long MaxFarFrames = 120;
        public const double InFlightTimeoutSeconds = 20;
        private const double LogEverySeconds = 60;

        private static int InFlight => SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config?.GridStreamsInFlight ?? 0;
        private static bool NearestFirst => SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config?.GridStreamNearestFirst == true;
        private static bool Logs => SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config?.StreamLogs == true;

        private static Func<object, MyClientStateBase> _state;
        private static Func<object, MyConcurrentDictionary<IMyReplicable, MyReplicableClientData>> _replicables;
        private static Func<object, MyStateDataEntry, bool> _queued;
        private static Action<object, MyStateDataEntry, long> _reschedule;
        private static Action<object, MyStateDataEntry, long> _enqueue;
        private static Func<MyReplicationServer, long> _syncFrame;

        private sealed class Flight
        {
            public IMyReplicable Replicable;
            public long Started;
            public int Pcu;
        }

        /// <summary>A client's counts since the last log line (and in total, for SentisTests).</summary>
        public sealed class Counts
        {
            public int Streamed, Ready, Waits, TimedOut;
            public long Pcu;
            public double ReadySeconds, ReadyMaxSeconds;
            public readonly Dictionary<string, int> Created = new Dictionary<string, int>();

            public void Add(Counts other)
            {
                Streamed += other.Streamed; Ready += other.Ready; Waits += other.Waits; TimedOut += other.TimedOut; Pcu += other.Pcu;
                ReadySeconds += other.ReadySeconds; ReadyMaxSeconds = Math.Max(ReadyMaxSeconds, other.ReadyMaxSeconds);
                foreach (var pair in other.Created) Created[pair.Key] = (Created.TryGetValue(pair.Key, out var n) ? n : 0) + pair.Value;
            }

            public override string ToString() =>
                $"{Streamed} grids streamed ({Pcu} PCU), {Ready} ready in {(Ready > 0 ? ReadySeconds / Ready : 0):0.00} s on average (at most {ReadyMaxSeconds:0.00} s)" +
                (TimedOut > 0 ? $", {TimedOut} not ready in {InFlightTimeoutSeconds:0} s" : "") + $", held back {Waits} times" +
                (Created.Count > 0 ? "; created without a stream: " + string.Join(", ", Created.OrderByDescending(p => p.Value).Select(p => p.Key + " " + p.Value)) : "");
        }

        // game thread (the replication server's update, its received packets and its adds)
        private static readonly Dictionary<Endpoint, List<Flight>> Flights = new Dictionary<Endpoint, List<Flight>>();
        private static readonly Dictionary<Endpoint, Counts> Recent = new Dictionary<Endpoint, Counts>();
        // a client's grid streams queued and not started yet: the nearest of them is started first
        private static readonly Dictionary<Endpoint, HashSet<MyStateDataEntry>> Waiting = new Dictionary<Endpoint, HashSet<MyStateDataEntry>>();
        private static MyReplicationServer _server;
        private static readonly Dictionary<Endpoint, Counts> Total = new Dictionary<Endpoint, Counts>();
        private static long _lastLog = Stopwatch.GetTimestamp();

        // Read by SentisTests.
        public static long Placed, Held, Started, Swapped;
        /// <summary>A grid's stream started to a client (its Steam id, the grid's entity id). For SentisTests.</summary>
        public static Action<ulong, long> StreamStarted;

        /// <summary>A client's counts since the start; null for a client never streamed anything. For SentisTests.</summary>
        public static Counts TotalOf(ulong steamId)
        {
            lock (Flights)
            {
                var all = new Counts();
                var any = false;
                foreach (var pair in Total)
                    if (pair.Key.Id.Value == steamId) { all.Add(pair.Value); any = true; }
                return any ? all : null;
            }
        }

        /// <summary>The grids of a client being loaded on it now. For SentisTests.</summary>
        public static int InFlightOf(ulong steamId)
        {
            lock (Flights) return Flights.Where(p => p.Key.Id.Value == steamId).Sum(p => p.Value.Count);
        }

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("GridStreamPacing", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var server = typeof(MyReplicationServer);
            var clientType = server.Assembly.GetType("VRage.Network.MyClient", true);
            var state = clientType.GetField("State", instance) ?? throw new MissingFieldException("MyClient.State");
            var replicables = clientType.GetField("Replicables", instance) ?? throw new MissingFieldException("MyClient.Replicables");
            var queue = clientType.GetField("DirtyQueue", instance) ?? throw new MissingFieldException("MyClient.DirtyQueue");

            var o = Expression.Parameter(typeof(object));
            var client = Expression.Convert(o, clientType);
            _state = Expression.Lambda<Func<object, MyClientStateBase>>(Expression.Field(client, state), o).Compile();
            _replicables = Expression.Lambda<Func<object, MyConcurrentDictionary<IMyReplicable, MyReplicableClientData>>>(
                Expression.Convert(Expression.Field(client, replicables), typeof(MyConcurrentDictionary<IMyReplicable, MyReplicableClientData>)), o).Compile();
            var entry = Expression.Parameter(typeof(MyStateDataEntry));
            var at = Expression.Parameter(typeof(long));
            var dirty = Expression.Field(client, queue);
            var contains = queue.FieldType.GetMethod("Contains") ?? throw new MissingMethodException("DirtyQueue.Contains");
            var update = queue.FieldType.GetMethod("UpdatePriority") ?? throw new MissingMethodException("DirtyQueue.UpdatePriority");
            var enqueue = queue.FieldType.GetMethod("Enqueue") ?? throw new MissingMethodException("DirtyQueue.Enqueue");
            _queued = Expression.Lambda<Func<object, MyStateDataEntry, bool>>(Expression.Call(dirty, contains, entry), o, entry).Compile();
            _reschedule = Expression.Lambda<Action<object, MyStateDataEntry, long>>(
                Expression.Call(dirty, update, entry, Expression.Convert(at, update.GetParameters()[1].ParameterType)), o, entry, at).Compile();
            _enqueue = Expression.Lambda<Action<object, MyStateDataEntry, long>>(
                Expression.IfThen(Expression.Not(Expression.Call(dirty, contains, entry)),
                    Expression.Call(dirty, enqueue, entry, Expression.Convert(at, enqueue.GetParameters()[1].ParameterType))), o, entry, at).Compile();
            var s = Expression.Parameter(server);
            var frame = typeof(MyReplicationLayer).GetField("SyncFrameCounter", instance) ?? throw new MissingFieldException("MyReplicationLayer.SyncFrameCounter");
            _syncFrame = Expression.Lambda<Func<MyReplicationServer, long>>(Expression.Field(s, frame), s).Compile();

            MethodInfo Own(string name) => typeof(GridStreamPacing).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo Game(string name) => server.GetMethod(name, instance) ?? throw new MissingMethodException("MyReplicationServer", name);
            // Torch suffixes and prefixes, as VoxelStreamNearestFirst and StreamingSerializeBudget on the same methods
            ctx.GetPattern(Game("ScheduleStateGroupSync")).Suffixes.Add(Own(nameof(SchedulePostfix)));
            ctx.GetPattern(Game("SendStreamingEntry")).Prefixes.Add(Own(nameof(SendStreamingEntryPrefix)));
            ctx.GetPattern(Game("ReplicableReady")).Suffixes.Add(Own(nameof(ReadySuffix)));
            ctx.GetPattern(Game("AddForClient")).Suffixes.Add(Own(nameof(AddForClientSuffix)));
        }

        // MyReplicationServer.ScheduleStateGroupSync(MyClient client, MyStateDataEntry groupEntry, long currentTime, bool allowReplicableRemoval)
        private static void SchedulePostfix(object client, MyStateDataEntry groupEntry, long currentTime)
        {
            try
            {
                if (groupEntry?.Group == null || !groupEntry.Group.IsStreaming || !(groupEntry.Owner is MyCubeGridReplicable owner)) return;
                var nearest = NearestFirst;
                if (nearest)
                {
                    var endpoint = _state(client)?.EndpointId;
                    // (not asked whether it started: on its add the game queues it before it makes the client's data, and the
                    // question throws then; one started is dropped from the set when the nearest is looked for)
                    if (endpoint.HasValue)
                        lock (Flights)
                        {
                            if (!Waiting.TryGetValue(endpoint.Value, out var set)) Waiting[endpoint.Value] = set = new HashSet<MyStateDataEntry>();
                            set.Add(groupEntry);
                        }
                }
                var held = InFlight > 0 && Busy(client, owner);
                if (!nearest && !held) return;
                if (!_queued(client, groupEntry)) return;
                // held: put off, the nearer still before the farther
                long? due = null;
                var at = _state(client)?.Position;
                var far = nearest && at.HasValue ? Far(owner, at.Value) : 0;
                if (nearest && at.HasValue) due = currentTime + 1 + far;
                if (held) due = currentTime + RetryFrames + far;
                if (!due.HasValue) return;
                _reschedule(client, groupEntry, due.Value);
                System.Threading.Interlocked.Increment(ref Placed);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "GridStreamPacing (schedule)");
            }
        }

        /// <summary>Frames a grid's stream is put later by for its distance: one for every <see cref="MetresPerFrame"/> m, <see cref="MaxFarFrames"/> at most.</summary>
        public static long Far(IMyReplicable grid, VRageMath.Vector3D at) =>
            Math.Min(MaxFarFrames, (long)(Math.Max(0, grid.GetAABB().Distance(at)) / MetresPerFrame));

        // MyReplicationServer.SendStreamingEntry(MyClient client, MyStateDataEntry entry): a grid's stream started here
        private static bool SendStreamingEntryPrefix(MyReplicationServer __instance, object client, MyStateDataEntry entry)
        {
            try
            {
                if (!(entry?.Owner is MyCubeGridReplicable owner)) return true;
                var state = _state(client);
                if (state == null || entry.Group.IsProcessingForClient(state.EndpointId) != MyStreamProcessingState.None) return true;
                if (InFlight > 0 && Busy(client, owner))
                {
                    Held++;
                    Of(Recent, state.EndpointId).Waits++;
                    Of(Total, state.EndpointId).Waits++;
                    // FilterStateSync would put it back by chance: a few frames on (the nearer before the farther), and
                    // the other streams first meanwhile
                    var at = state.Position;
                    var far = NearestFirst && at.HasValue ? Far(owner, at.Value) : 0;
                    _enqueue(client, entry, _syncFrame(__instance) + RetryFrames + far);
                    MaybeLog();
                    return false;
                }
                _server = __instance;
                if (NearestFirst && state.Position.HasValue && Nearer(client, state, entry, owner) is MyStateDataEntry nearer)
                {
                    // a nearer grid is waiting: it goes next frame, this one after it
                    Swapped++;
                    var frame = _syncFrame(__instance);
                    _enqueue(client, entry, frame + 1 + Far(owner, state.Position.Value));
                    _reschedule(client, nearer, frame);
                    return false;
                }
                lock (Flights)
                {
                    if (Waiting.TryGetValue(state.EndpointId, out var waiting)) waiting.Remove(entry);
                    if (!Flights.TryGetValue(state.EndpointId, out var list)) Flights[state.EndpointId] = list = new List<Flight>();
                    if (list.All(f => f.Replicable != owner))
                        list.Add(new Flight { Replicable = owner, Started = Stopwatch.GetTimestamp(), Pcu = owner.PCU ?? 0 });
                }
                Started++;
                StreamStarted?.Invoke(state.EndpointId.Id.Value, owner.Instance?.EntityId ?? 0);
                foreach (var counts in new[] { Of(Recent, state.EndpointId), Of(Total, state.EndpointId) })
                {
                    counts.Streamed++;
                    counts.Pcu += owner.PCU ?? 0;
                }
                MaybeLog();
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "GridStreamPacing (stream)");
            }
            return true;
        }

        /// <summary>
        /// The nearest of the client's grid streams waiting in its queue, when it is nearer than this one by more than
        /// <see cref="MetresPerFrame"/>; null when none is. Those started, out of the queue or gone are dropped first.
        /// </summary>
        private static MyStateDataEntry Nearer(object client, MyClientStateBase state, MyStateDataEntry entry, IMyReplicable grid)
        {
            lock (Flights)
            {
                if (!Waiting.TryGetValue(state.EndpointId, out var set) || set.Count == 0) return null;
                var at = state.Position.Value;
                var best = grid.GetAABB().Distance(at) - MetresPerFrame;
                MyStateDataEntry nearest = null;
                set.RemoveWhere(e => e == entry || e.Owner == null || !e.Owner.IsValid || !_queued(client, e) || !NotStarted(e, state.EndpointId));
                foreach (var e in set)
                {
                    var distance = e.Owner.GetAABB().Distance(at);
                    if (distance < best) { best = distance; nearest = e; }
                }
                return nearest;
            }
        }

        /// <summary>Whether the stream is not started for the client (false too when the client has no data of it yet).</summary>
        private static bool NotStarted(MyStateDataEntry entry, Endpoint endpoint)
        {
            try { return entry.Group.IsProcessingForClient(endpoint) == MyStreamProcessingState.None; }
            catch (NullReferenceException) { return false; }
        }

        /// <summary>
        /// Whether the client has as many grids loading as it may (the one it controls never waits): its streamed grids
        /// not ready yet, those gone from it, ready or over the timeout dropped first.
        /// </summary>
        private static bool Busy(object client, IMyReplicable grid)
        {
            var state = _state(client);
            if (state == null) return false;
            if (grid == state.ControlledReplicable || grid.GetParent() != null && grid.GetParent() == state.ControlledReplicable) return false;
            lock (Flights)
            {
                if (!Flights.TryGetValue(state.EndpointId, out var list) || list.Count == 0) return false;
                Prune(state.EndpointId, list, _replicables(client));
                return list.Count(f => f.Replicable != grid) >= InFlight;
            }
        }

        private static void Prune(Endpoint endpoint, List<Flight> list, MyConcurrentDictionary<IMyReplicable, MyReplicableClientData> held)
        {
            var now = Stopwatch.GetTimestamp();
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var flight = list[i];
                var seconds = (now - flight.Started) / (double)Stopwatch.Frequency;
                var known = held.TryGetValue(flight.Replicable, out var data);
                if (known && data.IsPending && seconds < InFlightTimeoutSeconds) continue;
                list.RemoveAt(i);
                if (!known) continue;   // gone from the client: out of range again, or removed
                foreach (var counts in new[] { Of(Recent, endpoint), Of(Total, endpoint) })
                {
                    if (data.IsPending) { counts.TimedOut++; continue; }
                    counts.Ready++;
                    counts.ReadySeconds += seconds;
                    counts.ReadyMaxSeconds = Math.Max(counts.ReadyMaxSeconds, seconds);
                }
            }
        }

        // MyReplicationServer.ReplicableReady(MyPacket packet): the time to ready taken when it comes
        private static void ReadySuffix(MyReplicationServer __instance, VRage.MyPacket packet)
        {
            try
            {
                lock (Flights)
                {
                    if (!Flights.TryGetValue(packet.Sender, out var list) || list.Count == 0) return;
                    var client = ClientOf(__instance, packet.Sender);
                    if (client != null) Prune(packet.Sender, list, _replicables(client));
                }
                MaybeLog();
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "GridStreamPacing (ready)");
            }
        }

        private static FieldInfo _clientStates;

        private static object ClientOf(MyReplicationServer server, Endpoint endpoint)
        {
            _clientStates = _clientStates ?? typeof(MyReplicationServer).GetField("m_clientStates", BindingFlags.Instance | BindingFlags.NonPublic);
            if (!(_clientStates?.GetValue(server) is System.Collections.IDictionary states)) return null;
            return states.Contains(endpoint) ? states[endpoint] : null;
        }

        // MyReplicationServer.AddForClient(IMyReplicable replicable, Endpoint clientEndpoint, MyClient client, bool force, bool addDependencies)
        private static void AddForClientSuffix(IMyReplicable replicable, Endpoint clientEndpoint)
        {
            try
            {
                if (replicable == null || replicable is IMyStreamableReplicable streamable && streamable.NeedsToBeStreamed) return;
                var name = replicable.GetType().Name;
                lock (Flights)
                    foreach (var counts in new[] { Of(Recent, clientEndpoint), Of(Total, clientEndpoint) })
                        counts.Created[name] = (counts.Created.TryGetValue(name, out var n) ? n : 0) + 1;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "GridStreamPacing (add)");
            }
        }

        private static Counts Of(Dictionary<Endpoint, Counts> map, Endpoint endpoint)
        {
            lock (Flights)
            {
                if (!map.TryGetValue(endpoint, out var counts)) map[endpoint] = counts = new Counts();
                return counts;
            }
        }

        /// <summary>Once a minute with the stream logs on: a line for each client that was sent anything.</summary>
        private static void MaybeLog()
        {
            var now = Stopwatch.GetTimestamp();
            if ((now - _lastLog) / (double)Stopwatch.Frequency < LogEverySeconds) return;
            _lastLog = now;
            lock (Flights)
            {
                if (Logs)
                    foreach (var pair in Recent.Where(p => p.Value.Streamed > 0 || p.Value.Created.Count > 0))
                        SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Info(
                            $"Streams to {Name(pair.Key.Id.Value)} ({pair.Key.Id.Value}) in the last minute: {pair.Value}" +
                            (Flights.TryGetValue(pair.Key, out var list) && list.Count > 0 ? $"; {list.Count} loading now" : ""));
                Recent.Clear();
                if (_server != null)
                    foreach (var gone in Waiting.Keys.Concat(Flights.Keys).Where(e => ClientOf(_server, e) == null).ToList())
                    {
                        Waiting.Remove(gone);
                        Flights.Remove(gone);
                    }
            }
        }

        private static string Name(ulong steamId)
        {
            try { return Sandbox.Game.World.MySession.Static?.Players?.TryGetIdentityNameFromSteamId(steamId) ?? "?"; }
            catch { return "?"; }
        }
    }
}
