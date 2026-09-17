using System;
using System.Diagnostics;
using System.Reflection;
using Sandbox;
using Torch.Managers.PatchManager;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Decides when to run a gen0 collection ourselves. .NET Framework has no pauseless GC: every
    /// gen0/gen1 collection stops the game thread for several milliseconds, and left alone it fires
    /// in whatever frame allocates past the budget - often a frame that is already heavy. The
    /// decider learns how far the heap grows before a natural gen0 and, once most of that budget
    /// is used, asks for a collection at the end of a light frame, where the pause fits into the
    /// frame's unused time. Pure logic, no GC calls, so it can be tested.
    /// </summary>
    public sealed class GcScheduleDecider
    {
        /// <summary>Frames with more simulation work than this are never used for a collection.</summary>
        public const double LightFrameMs = 4.0;

        /// <summary>Share of the learned natural budget after which a light frame collects.</summary>
        public const double BudgetShare = 0.6;

        private const double BudgetSmoothing = 0.25;

        private long _lastGen0 = -1;
        private long _baselineHeap;
        private long _lastGrowth;
        private double _naturalBudget;
        private bool _lastCollectionScheduled;

        /// <returns>true when a gen0 collection should run now.</returns>
        public bool OnFrameEnd(long gen0Count, long heapBytes, double workMs)
        {
            if (gen0Count != _lastGen0)
            {
                if (_lastGen0 >= 0 && !_lastCollectionScheduled && _lastGrowth > 0)
                    _naturalBudget = _naturalBudget <= 0
                        ? _lastGrowth
                        : _naturalBudget + BudgetSmoothing * (_lastGrowth - _naturalBudget);
                _lastGen0 = gen0Count;
                _baselineHeap = heapBytes;
                _lastGrowth = 0;
                _lastCollectionScheduled = false;
                return false;
            }

            _lastGrowth = heapBytes - _baselineHeap;
            return _naturalBudget > 0 && workMs <= LightFrameMs && _lastGrowth >= BudgetShare * _naturalBudget;
        }

        /// <summary>Call right after a collection that this decider asked for.</summary>
        public void OnCollectedByScheduler()
        {
            _lastCollectionScheduled = true;
        }
    }

    [PatchShim]
    public static class GcScheduler
    {
        private static readonly GcScheduleDecider Decider = new GcScheduleDecider();
        private static long _frameStart;

        // Read by the SentisTests frame probe: its own frame timer may stop before this suffix runs,
        // so the scheduled pause is measured here, together with the frame it was added to.
        public static long Collections;
        public static double TotalPauseMs;
        public static double MaxPauseMs;
        public static long FramesOverBudgetWithPause;

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("GcScheduler", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var update = typeof(MySandboxGame).GetMethod("Update",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
            var pattern = ctx.GetPattern(update);
            pattern.Prefixes.Add(typeof(GcScheduler).GetMethod(nameof(UpdatePrefix), BindingFlags.Static | BindingFlags.NonPublic));
            pattern.Suffixes.Add(typeof(GcScheduler).GetMethod(nameof(UpdateSuffix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static void UpdatePrefix()
        {
            _frameStart = Stopwatch.GetTimestamp();
        }

        private static void UpdateSuffix()
        {
            if (_frameStart == 0) return;
            try
            {
                var workMs = (Stopwatch.GetTimestamp() - _frameStart) * 1000.0 / Stopwatch.Frequency;
                if (!Decider.OnFrameEnd(GC.CollectionCount(0), GC.GetTotalMemory(false), workMs)) return;
                var pauseStart = Stopwatch.GetTimestamp();
                GC.Collect(0, GCCollectionMode.Forced, blocking: true);
                Decider.OnCollectedByScheduler();
                var pauseMs = (Stopwatch.GetTimestamp() - pauseStart) * 1000.0 / Stopwatch.Frequency;
                Collections++;
                TotalPauseMs += pauseMs;
                if (pauseMs > MaxPauseMs) MaxPauseMs = pauseMs;
                if (workMs + pauseMs > 1000.0 / 60.0) FramesOverBudgetWithPause++;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "GcScheduler failed");
            }
        }
    }
}
