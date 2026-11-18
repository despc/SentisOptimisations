using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Sandbox;
using Sandbox.Game.Entities.Blocks;

namespace SentisOptimisationsPlugin
{
    /// <summary>What a programmable block costs the server, and whether that is too much.</summary>
    public enum PbVerdict
    {
        /// <summary>Within its budget.</summary>
        Ok,

        /// <summary>Over a threshold, but not often enough yet to be punished.</summary>
        Warned,

        /// <summary>Over a threshold in too many of its recent runs.</summary>
        Punish
    }

    /// <summary>
    /// The load every programmable block puts on the game thread.
    ///
    /// Two numbers are kept per block, because they answer different questions:
    /// <list type="bullet">
    /// <item>the <b>time of one run</b>, which says whether the block can stall a single frame;</item>
    /// <item>the <b>time per frame</b>, which says how much of the game thread the block owns. It is
    /// the run divided by the frames since the block last ran, averaged. A script on Update1 taking
    /// 0.8 ms costs 0.8 ms of every frame and never trips a per-run threshold; a script on Update100
    /// taking 3 ms costs 0.03 ms a frame and trips it every time. Judged on runs alone, the guard
    /// punishes the second and never notices the first.</item>
    /// </list>
    ///
    /// Both are measured in microseconds from <see cref="System.Diagnostics.Stopwatch"/> timestamps -
    /// whole milliseconds cannot tell 1.9 ms of every frame from 1.0 ms - and a run during which the
    /// garbage collector ran is dropped instead of being charged to the script, which is where most
    /// of the false readings came from.
    ///
    /// The verdict looks at a window of the last <see cref="WindowRuns"/> runs rather than at a
    /// counter that only ever grows: a block is punished for being over its budget in most of its
    /// recent runs, not for having been over it a few times since the world was loaded.
    /// </summary>
    public static class PbLoad
    {
        /// <summary>Runs kept in the window a verdict is made from.</summary>
        public const int WindowRuns = 20;

        /// <summary>
        /// The time per frame is the time of the runs in the last second (this many frames), divided by the second. It was a
        /// running average forgetting nine tenths of a run in a third of a second: a script doing its heavy part now and
        /// then (RTAS: its targets every 8 ticks, its blocks gathered anew every 5 s) was over a 0.3 ms budget for a handful
        /// of frames each time, which is all the verdict needs, while it cost the server a few hundredths of a millisecond a
        /// frame over the second. A script over its budget on every frame is over it within the second, as before.
        /// </summary>
        public const int LoadFrames = 60;

        /// <summary>The runs kept for the second: more than one a frame is a script run by others too.</summary>
        private const int LoadRunsKept = 128;

        /// <summary>Runs needed before the per-frame average is trusted enough to punish by it.</summary>
        private const int RunsBeforeLoadCounts = 10;


        /// <summary>
        /// The first runs of a script instance, not measured at all: its first calls into collections of its own types are
        /// compiled to machine code then (what PbCompile cannot prepare ahead), and it sets itself up. Isy's Inventory
        /// Manager, compiled ahead: 3.6, 6.1 and 10.4 ms in its first twelve runs, never over 2 ms after - three over the
        /// limit, one short of being taken apart (stand, scenario pb_iim, 04.10.2026). A count of runs, not a time: each run is
        /// cut off by the game's instruction limit, so ten of them cannot hold the server for long.
        /// </summary>
        public const int UnmeasuredRuns = 10;

        /// <summary>
        /// Frames after a script instance starts (compiled, switched on) in which its load per frame is not judged - its
        /// single runs are, by the per-run limit. A script sets itself up then: RTAS gathers its blocks in its constructor and
        /// starts its scans, over the load budget for seconds, and was taken apart on every recompile. Its average is kept
        /// from its first measured run all the same.
        /// </summary>
        public const ulong LoadGraceFrames = 600;

        /// <summary>
        /// A block gets the grace once in this many frames: a script recompiled again and again does not stay unjudged.
        /// </summary>
        public const ulong LoadGraceEveryFrames = 3600;

        /// <summary>A block that has not run for this many frames is no longer counted as a load.</summary>
        private const ulong IdleFrames = 600;

        private const uint WindowMask = (1u << WindowRuns) - 1u;

        public sealed class Stats
        {
            public double LoadMsPerFrame;
            public double LastMs;
            public double PeakMs;
            public ulong LastFrame;
            public int Runs;
            public uint Window;
            public DateTime LastWarned;
            public object Program;          // the script instance the runs are of: a new one (recompiled) begins anew
            public int LoadSamples;         // runs in the per-frame average (the first run of an instance is not one)
            public ulong StartFrame;        // the first run of the instance
            public bool Graced;             // its load is not judged until LoadGraceFrames after StartFrame
            public ulong GracedAt;          // the start of the block's last grace (kept over the instances: once a minute)
            public bool HadGrace;
            // the measured runs of the last LoadFrames frames, oldest first (a ring)
            public readonly ulong[] RunFrames = new ulong[LoadRunsKept];
            public readonly double[] RunTimes = new double[LoadRunsKept];
            public int RunsHead, RunsCount;
            public double RunsSum;
        }

        private static readonly ConcurrentDictionary<MyProgrammableBlock, Stats> Blocks =
            new ConcurrentDictionary<MyProgrammableBlock, Stats>();

        /// <summary>
        /// Takes one run of <paramref name="pb"/> into account and says what to do about it.
        /// </summary>
        /// <param name="ms">How long the run took, in milliseconds.</param>
        /// <param name="noisy">
        /// True when the garbage collector ran during the measurement, so the time is not the
        /// script's own: the run still counts as a run, its time is thrown away.
        /// </param>
        /// <param name="maxRunMs">Threshold for a single run.</param>
        /// <param name="maxLoadMsPerFrame">Threshold for the time per frame; 0 disables it.</param>
        /// <param name="overrunsBeforePunish">Overruns inside the window that are still tolerated.</param>
        public static PbVerdict Record(MyProgrammableBlock pb, double ms, bool noisy,
            double maxRunMs, double maxLoadMsPerFrame, int overrunsBeforePunish, object program = null)
        {
            var stats = Blocks.GetOrAdd(pb, _ => new Stats());
            return Record(stats, MySandboxGame.Static.SimulationFrameCounter, ms, noisy, maxRunMs, maxLoadMsPerFrame, overrunsBeforePunish, program);
        }

        /// <summary><see cref="Record(MyProgrammableBlock, double, bool, double, double, int, object)"/> on a block's counters at a frame (for tests).</summary>
        public static PbVerdict Record(Stats stats, ulong frame, double ms, bool noisy,
            double maxRunMs, double maxLoadMsPerFrame, int overrunsBeforePunish, object program)
        {
            if (program != null && !ReferenceEquals(program, stats.Program))
            {
                // another script instance (recompiled, switched off and on): its own record
                stats.Program = program;
                stats.Runs = 0;
                stats.Window = 0;
                stats.LoadMsPerFrame = 0;
                stats.LastFrame = 0;
                stats.LoadSamples = 0;
                stats.RunsHead = stats.RunsCount = 0;
                stats.RunsSum = 0;
            }
            // (no frames since an earlier run for the first run of an instance: its time is not spread over frames it
            // did not wait, and it does not start the per-frame average - taken as a run every frame, the 3 ms first run
            // of Isy's Inventory Manager put it at 3 ms of every frame, still over the limit ten runs on)
            var firstRun = stats.LastFrame == 0;
            if (firstRun)
            {
                stats.StartFrame = frame;
                stats.Graced = !stats.HadGrace || frame - stats.GracedAt >= LoadGraceEveryFrames;
                if (stats.Graced)
                {
                    stats.HadGrace = true;
                    stats.GracedAt = frame;
                }
            }
            var frames = firstRun || frame <= stats.LastFrame ? 1UL : frame - stats.LastFrame;
            stats.LastFrame = frame;
            stats.Runs++;

            if (stats.Runs <= UnmeasuredRuns) return PbVerdict.Ok;
            if (noisy) return PbVerdict.Ok;
            stats.LastMs = ms;
            if (ms > stats.PeakMs) stats.PeakMs = ms;
            if (!firstRun)
            {
                stats.LoadSamples++;
                stats.LoadMsPerFrame = Second(stats, frame, ms, frames);
            }

            var inGrace = stats.Graced && frame - stats.StartFrame < LoadGraceFrames;
            var overLoad = maxLoadMsPerFrame > 0 && !inGrace && stats.LoadSamples >= RunsBeforeLoadCounts &&
                           stats.LoadMsPerFrame > maxLoadMsPerFrame;
            var over = ms > maxRunMs || overLoad;
            stats.Window = ((stats.Window << 1) | (over ? 1u : 0u)) & WindowMask;
            if (!over) return PbVerdict.Ok;

            return Overruns(stats.Window) > overrunsBeforePunish ? PbVerdict.Punish : PbVerdict.Warned;
        }

        /// <summary>
        /// The time per frame after a run: the runs of the last <see cref="LoadFrames"/> frames over those frames - all of them,
        /// also in the first second of an instance (counted over the frames since its start, its setup was a second's load
        /// in a few frames: RTAS recompiled twice in half a minute, its second instance out of its grace, 0.43 ms of "every
        /// frame" for a second that cost 0.27). A script that runs less often than that - a missile grid that wakes now
        /// and then - is its run over the frames since the one before: one old spike is not carried into the cheap runs after
        /// it (production, 31.10.2026: a block destroyed with "run 0.00 ms, 3 ms of every frame").
        /// </summary>
        private static double Second(Stats stats, ulong frame, double ms, ulong framesSincePrevious)
        {
            // the runs out of the second go, then this one comes (the oldest goes for it when the ring is full)
            while (stats.RunsCount > 0 && stats.RunFrames[stats.RunsHead] + LoadFrames <= frame) Drop(stats);
            if (stats.RunsCount == LoadRunsKept) Drop(stats);
            var at = (stats.RunsHead + stats.RunsCount) % LoadRunsKept;
            stats.RunFrames[at] = frame;
            stats.RunTimes[at] = ms;
            stats.RunsCount++;
            stats.RunsSum += ms;
            if (stats.RunsCount == 1) return ms / Math.Max(1UL, framesSincePrevious);
            return stats.RunsSum / LoadFrames;
        }

        private static void Drop(Stats stats)
        {
            stats.RunsSum -= stats.RunTimes[stats.RunsHead];
            stats.RunsHead = (stats.RunsHead + 1) % LoadRunsKept;
            stats.RunsCount--;
            if (stats.RunsCount == 0) stats.RunsSum = 0;
        }

        /// <summary>
        /// True at most once per <paramref name="every"/> for a block, so the warning its owner gets
        /// is a warning and not a stream: a script on Update1 is over its budget sixty times a second.
        /// </summary>
        public static bool WarnDue(MyProgrammableBlock pb, TimeSpan every)
        {
            if (!Blocks.TryGetValue(pb, out var stats)) return false;
            var now = DateTime.UtcNow;
            if (now - stats.LastWarned < every) return false;
            stats.LastWarned = now;
            return true;
        }

        /// <summary>How many of the recent runs of <paramref name="pb"/> were over a threshold.</summary>
        public static int Overruns(MyProgrammableBlock pb) =>
            Blocks.TryGetValue(pb, out var stats) ? Overruns(stats.Window) : 0;

        /// <summary>The share of the game thread the block owns, in milliseconds of every frame.</summary>
        public static double LoadMsPerFrame(MyProgrammableBlock pb) =>
            Blocks.TryGetValue(pb, out var stats) && !Idle(stats) ? stats.LoadMsPerFrame : 0;

        /// <summary>The last run of the block, in milliseconds.</summary>
        public static double LastMs(MyProgrammableBlock pb) =>
            Blocks.TryGetValue(pb, out var stats) ? stats.LastMs : 0;

        /// <summary>
        /// The heaviest blocks, as "name (grid) 0.42 ms/frame" - what the server pays for scripts,
        /// which nothing reported before: only runs over the per-run threshold were ever logged.
        /// </summary>
        public static string Top(int count)
        {
            var top = Blocks.Where(pair => !pair.Key.Closed && !Idle(pair.Value))
                .OrderByDescending(pair => pair.Value.LoadMsPerFrame)
                .Take(count)
                .Select(pair => $"{pair.Key.CustomName} ({pair.Key.CubeGrid?.DisplayName}) " +
                                $"{pair.Value.LoadMsPerFrame:F2} ms/frame, run {pair.Value.LastMs:F2} ms")
                .ToList();
            return top.Count == 0 ? "none" : string.Join("; ", top);
        }

        /// <summary>
        /// Every block that runs a script, one per line, by its name (then its grid's, then its id: the order does not
        /// change from one refresh to the next): its name and grid, what it costs a frame, its last run, its worst run and
        /// how many of its recent runs were over a threshold. By the load the lines jumped about every second.
        /// </summary>
        public static string Report()
        {
            var rows = Blocks.Where(pair => !pair.Key.Closed && !Idle(pair.Value))
                .Select(pair => (Block: pair.Key, Stats: pair.Value, Name: $"{pair.Key.CustomName} ({pair.Key.CubeGrid?.DisplayName})"))
                .OrderBy(row => row.Block.CustomName?.ToString() ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Block.CubeGrid?.DisplayName ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Block.EntityId)
                .ToList();
            if (rows.Count == 0) return "No script is running.";
            var width = Math.Min(60, rows.Max(row => row.Name.Length));
            return string.Join(Environment.NewLine, rows.Select(row =>
                $"{(row.Name.Length > width ? row.Name.Substring(0, width - 1) + "~" : row.Name.PadRight(width))}   " +
                $"{row.Stats.LoadMsPerFrame,6:F2} ms/frame   run {row.Stats.LastMs,6:F2} ms   " +
                $"peak {row.Stats.PeakMs,6:F2} ms   over {Overruns(row.Stats.Window)}/{WindowRuns}"));
        }

        /// <summary>How many blocks have run a script recently.</summary>
        public static int Running() => Blocks.Count(pair => !pair.Key.Closed && !Idle(pair.Value));

        /// <summary>The total the running scripts cost one frame.</summary>
        public static double TotalMsPerFrame()
        {
            double total = 0;
            foreach (var pair in Blocks)
                if (!pair.Key.Closed && !Idle(pair.Value))
                    total += pair.Value.LoadMsPerFrame;
            return total;
        }

        public static void Forget(MyProgrammableBlock pb) => Blocks.TryRemove(pb, out _);

        public static void Clear() => Blocks.Clear();

        private static bool Idle(Stats stats) =>
            MySandboxGame.Static == null || MySandboxGame.Static.SimulationFrameCounter - stats.LastFrame > IdleFrames;

        private static int Overruns(uint window)
        {
            var count = 0;
            while (window != 0)
            {
                window &= window - 1;
                count++;
            }

            return count;
        }
    }
}
