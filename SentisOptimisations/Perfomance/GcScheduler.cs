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
    /// decider learns how far allocations grow before a natural gen0 and, once most of that budget
    /// is used, asks for a collection in a frame that is lighter than usual and has room for the
    /// estimated pause. A fixed "light frame" threshold does not work: on a loaded server even the
    /// median frame can exceed any constant, and the scheduler then never fires (measured: 0
    /// collections in a 60 s window with a median of 10.6 ms and a 4 ms threshold). Hence the
    /// threshold is the median of recent frames, and the pause estimate is learned from our own
    /// collections.
    ///
    /// Two guards learned from the 64-client bench: when collections are inherently expensive
    /// (joining clients, gen0 pauses of tens of ms) scheduling every opportunity only adds to the
    /// stalls, so above a pause ceiling the scheduler throttles to a rare probe that keeps the
    /// estimate fresh - never a full stand-down, because an estimate that is never re-measured
    /// stays high forever and the scheduler locks itself out (measured: estimate stuck at 18 ms
    /// in a window where 10 ms frames wanted collecting). The estimate also decays towards its
    /// initial value while the heap is quiet (no natural collection for a while).
    /// Pure logic, no GC calls, so it can be tested.
    /// </summary>
    public sealed class GcScheduleDecider
    {
        /// <summary>Frame budget the collection pause must fit into together with the frame's own work.</summary>
        public const double FrameBudgetMs = 1000.0 / 60.0;

        /// <summary>Share of the learned natural budget after which a suitable frame collects.</summary>
        public const double BudgetShare = 0.6;

        /// <summary>Starting estimate of our collection pause, corrected by every real one.</summary>
        public const double InitialPauseEstimateMs = 4.0;

        /// <summary>Above this estimated pause collections are throttled to probes.</summary>
        public const double MaxScheduledPauseMs = 10.0;

        /// <summary>Frames between probe collections while the pause estimate is above the ceiling.</summary>
        public const int ProbeIntervalFrames = 600;

        /// <summary>Minimum frames between scheduled collections; prevents storms during joins.</summary>
        public const int MinIntervalFrames = 30;

        /// <summary>Per-frame pull of the estimate back to its start while the heap is quiet.</summary>
        private const double PauseDecay = 0.004;

        /// <summary>How long without a natural collection counts as a quiet heap for the decay.</summary>
        private const int QuietFrames = 30;

        private const double PauseSmoothing = 0.3;
        private const double BudgetSmoothing = 0.25;

        /// <summary>Recent frame work times; the median of this is the lightness threshold.</summary>
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
        private int _framesSinceNaturalGen0;
        private readonly double[] _recent = new double[MedianWindow];
        private readonly double[] _scratch = new double[MedianWindow];
        private int _recentNext;
        private int _recentCount;

        /// <returns>true when a gen0 collection should run now.</returns>
        /// <param name="gen0Count"><c>GC.CollectionCount(0)</c> at the end of the frame.</param>
        /// <param name="allocatedBytes">
        /// Cumulative allocations of the game thread; growth between natural gen0 collections is
        /// what the budget is learned from (cheap counter, no heap walk, unlike GetTotalMemory).
        /// </param>
        /// <param name="workMs">Simulation work in the frame that just ended, without the pause.</param>
        public bool OnFrameEnd(long gen0Count, long allocatedBytes, double workMs)
        {
            if (_framesSinceNaturalGen0 < int.MaxValue) _framesSinceNaturalGen0++;
            if (_framesSinceCollect < int.MaxValue) _framesSinceCollect++;
            if (_pauseEstimateMs > InitialPauseEstimateMs && _framesSinceNaturalGen0 >= QuietFrames)
                _pauseEstimateMs += PauseDecay * (InitialPauseEstimateMs - _pauseEstimateMs);

            if (gen0Count != _lastGen0)
            {
                if (_lastGen0 >= 0 && _lastGrowth > 0)
                    _naturalBudget = _naturalBudget <= 0
                        ? _lastGrowth
                        : _naturalBudget + BudgetSmoothing * (_lastGrowth - _naturalBudget);
                _lastGen0 = gen0Count;
                _baselineAllocated = allocatedBytes;
                _lastGrowth = 0;
                _framesSinceNaturalGen0 = 0;
                AddSample(workMs);
                return false;
            }

            _lastGrowth = allocatedBytes - _baselineAllocated;
            _lastAllocated = allocatedBytes;
            // Above the pause ceiling: only the rare probe, which ignores the frame-fit test (a
            // probe's point is re-measuring the pause, and no frame fits a 20 ms estimate).
            var collect = _naturalBudget > 0 && _lastGrowth >= BudgetShare * _naturalBudget &&
                          _recentCount >= MedianWarmup && workMs <= MedianOfRecent() &&
                          (_pauseEstimateMs > MaxScheduledPauseMs
                              ? _framesSinceCollect >= ProbeIntervalFrames
                              : _framesSinceCollect >= MinIntervalFrames &&
                                workMs + _pauseEstimateMs <= FrameBudgetMs);
            AddSample(workMs);
            return collect;
        }

        /// <summary>Call right after a collection that this decider asked for, with its measured pause.</summary>
        /// <remarks>
        /// A scheduled collection does not change the allocation counter, so the baseline is reset
        /// here: the next budget window starts from the now-cleaner heap.
        /// </remarks>
        public void OnCollectedByScheduler(double pauseMs)
        {
            _baselineAllocated = _lastAllocated;
            _lastGrowth = 0;
            _framesSinceCollect = 0;
            if (pauseMs <= 0) return;
            _pauseEstimateMs += PauseSmoothing * (pauseMs - _pauseEstimateMs);
        }

        // TEMPORARY DIAGNOSTICS while debugging why the scheduler never fires on the bench.
        public string Diagnostic() =>
            $"budget={_naturalBudget / 1048576.0:F2}MB growth={_lastGrowth / 1048576.0:F2}MB frames={_recentCount}/{MedianWarmup} median={(_recentCount > 0 ? MedianOfRecent() : 0):F1}ms pauseEst={_pauseEstimateMs:F1}ms sinceNatural={_framesSinceNaturalGen0} sinceCollect={_framesSinceCollect}";

        private void AddSample(double workMs)
        {
            _recent[_recentNext] = workMs;
            _recentNext = (_recentNext + 1) % MedianWindow;
            if (_recentCount < MedianWindow) _recentCount++;
        }

        // Called only once the growth condition already passed, so copying the window is rare.
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
        private static long _diagCounter;

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
                if (++_diagCounter % 900 == 0)
                    SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Info(
                        $"GcScheduler DIAG frame={_diagCounter} work={workMs:F1}ms {Decider.Diagnostic()}");
                if (!Decider.OnFrameEnd(GC.CollectionCount(0), GC.GetAllocatedBytesForCurrentThread(), workMs)) return;
                var pauseStart = Stopwatch.GetTimestamp();
                GC.Collect(0, GCCollectionMode.Forced, blocking: true);
                var pauseMs = (Stopwatch.GetTimestamp() - pauseStart) * 1000.0 / Stopwatch.Frequency;
                Decider.OnCollectedByScheduler(pauseMs);
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
