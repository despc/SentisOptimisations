using System;
using System.Diagnostics;
using System.Reflection;
using Sandbox;
using Torch.Managers.PatchManager;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Decides when to run a gen0 collection ourselves. .NET Framework has no pauseless GC: every
    /// gen0/gen1 collection stops the game thread, and left alone it fires in whatever frame
    /// allocates past the budget - often a frame that is already heavy. The decider learns how far
    /// allocations grow before a natural gen0 and, once most of that budget is used, asks for a
    /// collection in a frame that is lighter than usual and still has room for the pause.
    ///
    /// Lightness is relative: a fixed threshold never fires on a loaded server (measured: 0
    /// collections in a 60 s window with a median of 10.6 ms and a 4 ms threshold), so a frame
    /// qualifies when it is at or below the median of recent frames.
    ///
    /// What a collection costs depends on what survives it, not on how much garbage there is, so
    /// collecting earlier does not make it cheaper; it only moves the pause. With 64 clients a
    /// forced gen0 cost as much as a natural one (16 ms against about 14.5 ms), while on an empty
    /// server it cost 2 ms. A collection is therefore only scheduled where the estimated pause fits
    /// into the frame, and under heavy load the scheduler stays out of the way. The estimate is
    /// learned from our own collections and from natural ones - a frame with a natural gen0 is
    /// longer than the median by about the pause - so it follows the load in both directions
    /// without ever forcing a collection just to measure it.
    /// Pure logic, no GC calls, so it can be tested.
    /// </summary>
    public sealed class GcScheduleDecider
    {
        /// <summary>Frame budget the collection pause must fit into together with the frame's own work.</summary>
        public const double FrameBudgetMs = 1000.0 / 60.0;

        /// <summary>Share of the learned natural budget after which a suitable frame collects.</summary>
        public const double BudgetShare = 0.6;

        /// <summary>Starting estimate of a gen0 pause, corrected by every observed one.</summary>
        public const double InitialPauseEstimateMs = 4.0;

        /// <summary>Minimum frames between scheduled collections.</summary>
        public const int MinIntervalFrames = 30;

        private const double PauseSmoothing = 0.3;
        private const double BudgetSmoothing = 0.25;

        /// <summary>Recent frame work times without a collection; their median is the lightness threshold.</summary>
        private const int MedianWindow = 128;

        /// <summary>Median is trusted once at least this many frames are in the window.</summary>
        private const int MedianWarmup = MedianWindow / 4;

        private long _lastGen0 = -1;
        private long _baselineAllocated;
        private long _lastAllocated;
        private long _lastGrowth;
        private double _naturalBudget;
        private double _pauseEstimateMs = InitialPauseEstimateMs;
        private int _framesSinceCollect = MinIntervalFrames;
        private readonly double[] _recent = new double[MedianWindow];
        private readonly double[] _scratch = new double[MedianWindow];
        private int _recentNext;
        private int _recentCount;

        public double PauseEstimateMs => _pauseEstimateMs;

        /// <returns>true when a gen0 collection should run now.</returns>
        /// <param name="gen0Count"><c>GC.CollectionCount(0)</c> at the end of the frame.</param>
        /// <param name="allocatedBytes">
        /// Cumulative allocations of the game thread; growth between natural gen0 collections is
        /// what the budget is learned from (cheap counter, no heap walk, unlike GetTotalMemory).
        /// </param>
        /// <param name="workMs">Work in the frame that just ended, including a natural collection
        /// if one happened in it, without a scheduled one.</param>
        public bool OnFrameEnd(long gen0Count, long allocatedBytes, double workMs)
        {
            if (_framesSinceCollect < int.MaxValue) _framesSinceCollect++;

            if (gen0Count != _lastGen0)
            {
                // A natural collection: our own ones are recorded in OnCollectedByScheduler.
                if (_lastGen0 >= 0)
                {
                    if (_lastGrowth > 0)
                        _naturalBudget = _naturalBudget <= 0
                            ? _lastGrowth
                            : _naturalBudget + BudgetSmoothing * (_lastGrowth - _naturalBudget);
                    // The frame it landed in is longer than usual by about the pause. The frame is
                    // not added to the median window, which stays a window of ordinary frames.
                    if (_recentCount >= MedianWarmup)
                        LearnPause(Math.Max(0, workMs - MedianOfRecent()));
                }
                _lastGen0 = gen0Count;
                _baselineAllocated = allocatedBytes;
                _lastAllocated = allocatedBytes;
                _lastGrowth = 0;
                return false;
            }

            _lastGrowth = allocatedBytes - _baselineAllocated;
            _lastAllocated = allocatedBytes;
            var collect = _naturalBudget > 0 && _lastGrowth >= BudgetShare * _naturalBudget &&
                          _framesSinceCollect >= MinIntervalFrames &&
                          workMs + _pauseEstimateMs <= FrameBudgetMs &&
                          _recentCount >= MedianWarmup && workMs <= MedianOfRecent();
            AddSample(workMs);
            return collect;
        }

        /// <summary>Call right after a collection that this decider asked for.</summary>
        /// <param name="pauseMs">Measured pause of the collection.</param>
        /// <param name="gen0CountAfter"><c>GC.CollectionCount(0)</c> right after it, so the next frame
        /// does not take our own collection for a natural one.</param>
        /// <remarks>
        /// A scheduled collection does not change the allocation counter, so the baseline is reset
        /// here: the next budget window starts from the now-cleaner heap.
        /// </remarks>
        public void OnCollectedByScheduler(double pauseMs, long gen0CountAfter)
        {
            _lastGen0 = gen0CountAfter;
            _baselineAllocated = _lastAllocated;
            _lastGrowth = 0;
            _framesSinceCollect = 0;
            if (pauseMs > 0) LearnPause(pauseMs);
        }

        private void LearnPause(double pauseMs)
        {
            _pauseEstimateMs += PauseSmoothing * (pauseMs - _pauseEstimateMs);
        }

        private void AddSample(double workMs)
        {
            _recent[_recentNext] = workMs;
            _recentNext = (_recentNext + 1) % MedianWindow;
            if (_recentCount < MedianWindow) _recentCount++;
        }

        // Called only after cheaper conditions passed, or on a natural collection, so the copy is rare.
        private double MedianOfRecent()
        {
            Array.Copy(_recent, _scratch, _recentCount);
            Array.Sort(_scratch, 0, _recentCount);
            return _scratch[(_recentCount - 1) / 2];
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
                if (!Decider.OnFrameEnd(GC.CollectionCount(0), GC.GetAllocatedBytesForCurrentThread(), workMs)) return;
                var pauseStart = Stopwatch.GetTimestamp();
                GC.Collect(0, GCCollectionMode.Forced, blocking: true);
                var pauseMs = (Stopwatch.GetTimestamp() - pauseStart) * 1000.0 / Stopwatch.Frequency;
                Decider.OnCollectedByScheduler(pauseMs, GC.CollectionCount(0));
                Collections++;
                TotalPauseMs += pauseMs;
                if (pauseMs > MaxPauseMs) MaxPauseMs = pauseMs;
                if (workMs + pauseMs > GcScheduleDecider.FrameBudgetMs) FramesOverBudgetWithPause++;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "GcScheduler failed");
            }
        }
    }
}
