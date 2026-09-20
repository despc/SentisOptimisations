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

        /// <summary>Weight of one run in the per-frame average.</summary>
        private const double Alpha = 0.1;

        /// <summary>Runs needed before the per-frame average is trusted enough to punish by it.</summary>
        private const int RunsBeforeLoadCounts = 10;

        /// <summary>A block that has not run for this many frames is no longer counted as a load.</summary>
        private const ulong IdleFrames = 600;

        private const uint WindowMask = (1u << WindowRuns) - 1u;

        private sealed class Stats
        {
            public double LoadMsPerFrame;
            public double LastMs;
            public double PeakMs;
            public ulong LastFrame;
            public int Runs;
            public uint Window;
            public DateTime LastWarned;
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
            double maxRunMs, double maxLoadMsPerFrame, int overrunsBeforePunish)
        {
            var stats = Blocks.GetOrAdd(pb, _ => new Stats());
            var frame = MySandboxGame.Static.SimulationFrameCounter;
            var frames = stats.LastFrame == 0 || frame <= stats.LastFrame ? 1UL : frame - stats.LastFrame;
            stats.LastFrame = frame;
            stats.Runs++;

            if (noisy) return PbVerdict.Ok;

            stats.LastMs = ms;
            if (ms > stats.PeakMs) stats.PeakMs = ms;
            var perFrame = ms / frames;
            stats.LoadMsPerFrame = stats.Runs == 1
                ? perFrame
                : stats.LoadMsPerFrame + (perFrame - stats.LoadMsPerFrame) * Alpha;

            var overLoad = maxLoadMsPerFrame > 0 && stats.Runs >= RunsBeforeLoadCounts &&
                           stats.LoadMsPerFrame > maxLoadMsPerFrame;
            var over = ms > maxRunMs || overLoad;
            stats.Window = ((stats.Window << 1) | (over ? 1u : 0u)) & WindowMask;
            if (!over) return PbVerdict.Ok;

            return Overruns(stats.Window) > overrunsBeforePunish ? PbVerdict.Punish : PbVerdict.Warned;
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
        /// The heaviest blocks, one per line: what each costs a frame, its last run, its worst run
        /// and how many of its recent runs were over a threshold.
        /// </summary>
        public static string Report(int count)
        {
            var lines = Blocks.Where(pair => !pair.Key.Closed && !Idle(pair.Value))
                .OrderByDescending(pair => pair.Value.LoadMsPerFrame)
                .Take(count)
                .Select(pair => $"{pair.Value.LoadMsPerFrame,6:F2} ms/frame   run {pair.Value.LastMs,6:F2} ms   " +
                                $"peak {pair.Value.PeakMs,6:F2} ms   over {Overruns(pair.Value.Window)}/{WindowRuns}   " +
                                $"{pair.Key.CustomName} ({pair.Key.CubeGrid?.DisplayName})")
                .ToList();
            return lines.Count == 0 ? "No script is running." : string.Join(Environment.NewLine, lines);
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
