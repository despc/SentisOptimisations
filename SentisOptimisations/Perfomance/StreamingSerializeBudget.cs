using System;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using Torch.Managers.PatchManager;
using VRage.Network;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Limits how much game-thread time per frame goes into starting streamed replicables (grids).
    ///
    /// When a grid has to be streamed to a client, MyReplicationServer.SendStreamingEntry builds the
    /// whole grid object builder for that client on the game thread (compression then runs in the
    /// background). For a large grid that is tens of milliseconds, and it is done separately for every
    /// client, so players joining or flying into a base make frames of 40-50 ms and more.
    ///
    /// The first stream of a frame always starts. After that, a stream that would build a new grid
    /// builder is put back into the client's dirty queue for the next frame once <see cref="BudgetMs"/>
    /// of building is done in this frame, and any stream is put back after <see cref="StreamBudgetMs"/>
    /// of streaming work; grids whose builder is kept (see GridStreamBuilders) only cost the refresh,
    /// so many more of them fit in a frame. Streams already being serialized or sent are not affected. Grids
    /// reach joining players a little later, but no frame does more than one large build on top of the
    /// budget.
    /// </summary>
    [PatchShim]
    public static class StreamingSerializeBudget
    {
        /// <summary>Game-thread time per frame for building new grid builders.</summary>
        public const double BudgetMs = 3.0;
        /// <summary>Game-thread time per frame for starting streams at all, cached ones included.</summary>
        public const double StreamBudgetMs = 6.0;

        private static readonly Type ClientType = typeof(MyReplicationServer).Assembly.GetType("VRage.Network.MyClient", true);
        private static readonly Func<object, MyClientStateBase> ClientState = BuildClientState();
        private static readonly Action<object, MyStateDataEntry, long> EnqueueDirty = BuildEnqueueDirty();
        private static readonly Func<MyReplicationServer, long> SyncFrame = BuildSyncFrame();
        private static readonly long BudgetTicks = (long)(BudgetMs * Stopwatch.Frequency / 1000.0);
        private static readonly long StreamBudgetTicks = (long)(StreamBudgetMs * Stopwatch.Frequency / 1000.0);

        private static long _frame = -1;
        private static long _buildTicks;
        private static long _streamTicks;
        private static int _started;
        // Torch has no __state; SendStreamingEntry runs on the game thread and is not re-entered.
        private static long _callStart;
        private static bool _building;

        public static long Deferred;

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("StreamingSerializeBudget", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var target = typeof(MyReplicationServer).GetMethod("SendStreamingEntry", BindingFlags.Instance | BindingFlags.NonPublic);
            var pattern = ctx.GetPattern(target);
            pattern.Prefixes.Add(typeof(StreamingSerializeBudget).GetMethod(nameof(SendStreamingEntryPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            pattern.Suffixes.Add(typeof(StreamingSerializeBudget).GetMethod(nameof(SendStreamingEntrySuffix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool SendStreamingEntryPrefix(MyReplicationServer __instance, object client, MyStateDataEntry entry)
        {
            _callStart = 0;
            var state = ClientState(client);
            // Only the call that would build the grid now is budgeted.
            if (entry.Group.IsProcessingForClient(state.EndpointId) != MyStreamProcessingState.None) return true;

            var frame = SyncFrame(__instance);
            NewFrame(frame);
            // A grid whose builder is kept only needs a cheap refresh, not a full build.
            var cached = SentisOptimisationsPlugin.GridStreamBuilders.IsReady(entry.Owner);
            var overBudget = cached
                ? _streamTicks >= StreamBudgetTicks
                : _buildTicks >= BudgetTicks || _streamTicks >= StreamBudgetTicks;
            if (_started > 0 && overBudget)
            {
                Deferred++;
                // FilterStateSync would reschedule it with a random delay of up to two send intervals;
                // a queued entry keeps the earlier priority, so it is retried next frame.
                EnqueueDirty(client, entry, frame + 1);
                return false;
            }
            _started++;
            _building = !cached;
            _callStart = Stopwatch.GetTimestamp();
            return true;
        }

        private static void SendStreamingEntrySuffix()
        {
            if (_callStart == 0) return;
            var spent = Stopwatch.GetTimestamp() - _callStart;
            _streamTicks += spent;
            if (_building) _buildTicks += spent;
            _callStart = 0;
        }

        private static void NewFrame(long frame)
        {
            if (frame == _frame) return;
            _frame = frame;
            _buildTicks = 0;
            _streamTicks = 0;
            _started = 0;
        }

        private static Func<object, MyClientStateBase> BuildClientState()
        {
            var client = Expression.Parameter(typeof(object), "client");
            var body = Expression.Field(Expression.Convert(client, ClientType), "State");
            return Expression.Lambda<Func<object, MyClientStateBase>>(body, client).Compile();
        }

        private static Action<object, MyStateDataEntry, long> BuildEnqueueDirty()
        {
            var client = Expression.Parameter(typeof(object), "client");
            var entry = Expression.Parameter(typeof(MyStateDataEntry), "entry");
            var priority = Expression.Parameter(typeof(long), "priority");
            var queue = Expression.Field(Expression.Convert(client, ClientType), "DirtyQueue");
            var body = Expression.IfThen(
                Expression.Not(Expression.Call(queue, "Contains", null, entry)),
                Expression.Call(queue, "Enqueue", null, entry, priority));
            return Expression.Lambda<Action<object, MyStateDataEntry, long>>(body, client, entry, priority).Compile();
        }

        private static Func<MyReplicationServer, long> BuildSyncFrame()
        {
            var server = Expression.Parameter(typeof(MyReplicationServer), "server");
            var field = typeof(MyReplicationLayer).GetField("SyncFrameCounter", BindingFlags.Instance | BindingFlags.NonPublic);
            return Expression.Lambda<Func<MyReplicationServer, long>>(Expression.Field(server, field), server).Compile();
        }
    }
}
