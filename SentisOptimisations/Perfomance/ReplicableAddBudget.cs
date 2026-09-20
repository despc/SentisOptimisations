using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using NLog;
using Sandbox;
using Torch.Managers.PatchManager;
using VRage.Network;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The entities a joining player is given are spread over frames instead of all in one.
    ///
    /// When a client arrives, <c>MyReplicationServer.UpdateBefore</c> walks every replicable of the
    /// client's update layers and calls <c>AddForClient</c> for each one it does not have yet - in a
    /// single frame. One such add costs about 0.9 ms (it registers the replicable, raises
    /// OnReplication and writes the creation packet, which serializes the entity for anything that is
    /// not streamed), so a join means one frame of a hundred milliseconds or more, and on a populated
    /// server far worse.
    ///
    /// Nothing about that work has to happen in one frame. The same loop runs again on the next
    /// frame and adds whatever the client still lacks, so a budget is enough: once
    /// <see cref="MsPerFrame"/> of adds have been made, the rest of this frame's adds are left for
    /// the next one. The player receives the world over a few frames instead of the server stopping.
    ///
    /// Two things are never delayed: an add the game asks for with <c>force</c> - the client's own
    /// character and what it is controlling - and the children of a replicable that is already being
    /// added, because only the parent's own call brings them in.
    /// </summary>
    [PatchShim]
    public static class ReplicableAddBudget
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>How long one frame may spend giving replicables to clients.</summary>
        private static float MsPerFrame => SentisOptimisationsPlugin.Config.ReplicableAddMsPerFrame;

        /// <summary>Adds put off to a later frame since the world was loaded.</summary>
        public static long Delayed;

        /// <summary>Adds that went through.</summary>
        public static long Added;

        /// <summary>An add over this is reported: it is one tree the budget cannot break up.</summary>
        private const double SlowAddMs = 10;

        private static ulong _frame;
        private static double _spentMs;

        [ThreadStatic] private static int _depth;
        [ThreadStatic] private static bool _skipped;
        [ThreadStatic] private static long _startedAt;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("ReplicableAddBudget", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var addForClient = typeof(MyReplicationServer).GetMethod("AddForClient",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (addForClient == null) throw new MissingMethodException("MyReplicationServer.AddForClient");

            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            var self = typeof(ReplicableAddBudget);
            var pattern = ctx.GetPattern(addForClient);
            pattern.Prefixes.Add(self.GetMethod(nameof(AddForClientPrefix), statics));
            pattern.Suffixes.Add(self.GetMethod(nameof(AddForClientSuffix), statics));

        }

        private static bool AddForClientPrefix(IMyReplicable replicable, bool force)
        {
            _skipped = false;

            // A child of a replicable that is already going in, or an add the game insists on.
            if (_depth > 0 || force)
            {
                _depth++;
                return true;
            }

            if (MsPerFrame <= 0 || MySandboxGame.Static == null)
            {
                _depth++;
                return true;
            }

            var frame = MySandboxGame.Static.SimulationFrameCounter;
            if (frame != _frame)
            {
                _frame = frame;
                _spentMs = 0;
            }

            if (_spentMs >= MsPerFrame)
            {
                // The loop that asked comes back next frame with whatever the client still lacks.
                Interlocked.Increment(ref Delayed);
                _skipped = true;
                return false;
            }

            _startedAt = Stopwatch.GetTimestamp();
            _depth++;
            return true;
        }

        private static void AddForClientSuffix(IMyReplicable replicable)
        {
            if (_skipped)
            {
                _skipped = false;
                return;
            }

            _depth--;
            if (_depth != 0) return;

            Interlocked.Increment(ref Added);
            if (_startedAt == 0) return;
            var ms = (Stopwatch.GetTimestamp() - _startedAt) * 1000.0 / Stopwatch.Frequency;
            _spentMs += ms;
            _startedAt = 0;
            if (ms > SlowAddMs)
            {
                Log.Info($"ReplicableAddBudget: one add of {replicable?.GetType().Name} took {ms:F0} ms " +
                         "with everything it brought in; the budget cannot split it");
            }
        }

        /// <summary>What the budget has done, for the statistics line.</summary>
        public static string Describe() =>
            $"{Interlocked.Read(ref Added)} replicables given to clients, {Interlocked.Read(ref Delayed)} adds moved to a later frame";
    }
}
