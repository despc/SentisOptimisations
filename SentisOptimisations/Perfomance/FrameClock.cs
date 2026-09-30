using System;
using System.Diagnostics;
using System.Reflection;
using Sandbox;
using Torch.Managers.PatchManager;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// How much of this frame's simulation is already done: the time since <c>MySandboxGame.Update</c> began. For work
    /// that can wait when the frame is already heavy: production rounds (<see cref="ProductionFrameBudget"/>), and the
    /// plugin's own per-frame budgets, which take no more than the frame has left (<see cref="Allowed"/>). Game thread.
    /// </summary>
    [PatchShim]
    public static class FrameClock
    {
        private static long _frameStart;

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("FrameClock", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var update = typeof(MySandboxGame).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null)
                         ?? throw new MissingMethodException("MySandboxGame", "Update");
            var pattern = ctx.GetPattern(update);
            pattern.Prefixes.Add(typeof(FrameClock).GetMethod(nameof(UpdatePrefix), BindingFlags.Static | BindingFlags.NonPublic));
            pattern.Suffixes.Add(typeof(FrameClock).GetMethod(nameof(UpdateSuffix), BindingFlags.Static | BindingFlags.NonPublic));
            Active = true;
        }

        /// <summary>Whether the clock runs (its patch took); without it <see cref="FrameEnded"/> never comes.</summary>
        public static bool Active { get; private set; }

        /// <summary>A frame from which this much is a spike, whatever the usual frame.</summary>
        public const double SpikeFloorMs = 33;

        /// <summary>A frame this many times the usual one is a spike.</summary>
        public const double SpikeFactor = 3;

        /// <summary>
        /// At the end of every frame's simulation, on the game thread: its time, and whether it is not a frame to judge
        /// anything by - a garbage collection in it, a world save going on, or a spike (a save's snapshot, a big grid
        /// spawned...).
        /// </summary>
        public static event Action<double, bool> FrameEnded;

        // statistics: frames ended, and why some were not ordinary
        public static long Frames, SpikeFrames, GcFrames, SaveFrames;

        private static int _gcAtStart;
        private static double _usualMs;

        private static void UpdatePrefix()
        {
            _frameStart = Stopwatch.GetTimestamp();
            _gcAtStart = GC.CollectionCount(0);
        }

        private static void UpdateSuffix()
        {
            try
            {
                var ms = ElapsedMs;
                var spike = IsSpike(ms, _usualMs);
                if (!spike) _usualMs = _usualMs == 0 ? ms : _usualMs * 0.98 + ms * 0.02;
                var gc = GC.CollectionCount(0) != _gcAtStart;
                var saving = Sandbox.Game.Screens.Helpers.MyAsyncSaving.InProgress;
                Frames++;
                if (spike) SpikeFrames++;
                if (gc) GcFrames++;
                if (saving) SaveFrames++;
                FrameEnded?.Invoke(ms, spike || gc || saving);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "FrameClock frame end");
            }
        }

        /// <summary>Whether a frame of <paramref name="ms"/> is a spike against the usual <paramref name="usualMs"/> (0: not known yet).</summary>
        public static bool IsSpike(double ms, double usualMs) => ms > Math.Max(SpikeFloorMs, SpikeFactor * usualMs);

        /// <summary>Where a frame's simulation should end, so that the rest of the frame fits into 16.7 ms.</summary>
        public const double TargetMs = 14;

        /// <summary>
        /// The time a piece of work that can wait may take in this frame: its own budget, but no more than the frame has
        /// left up to <see cref="TargetMs"/>, and never less than <paramref name="minMs"/> - a step forward every frame.
        /// </summary>
        public static double Allowed(double ownMs, double minMs) => Allowed(ownMs, minMs, ElapsedMs);

        public static double Allowed(double ownMs, double minMs, double elapsedMs) => Math.Max(minMs, Math.Min(ownMs, TargetMs - elapsedMs));

        /// <summary>Milliseconds of this frame's simulation so far; 0 before the first frame.</summary>
        public static double ElapsedMs => _frameStart == 0 ? 0 : (Stopwatch.GetTimestamp() - _frameStart) * 1000.0 / Stopwatch.Frequency;
    }
}
