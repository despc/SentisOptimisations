using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Sandbox.Engine.Physics;
using Torch.Managers.PatchManager;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// How long the physics step of the game thread takes, measured continuously and for free.
    ///
    /// Measured is <c>MyPhysics.Simulate</c>: the ray casts, the cluster upkeep, the Havok step of
    /// every cluster and the work queued behind it - everything physics costs the game thread. Per
    /// cluster there is nothing to read: with Havok's parallel scheduling, the default on a dedicated
    /// server, all active clusters go through one job queue. So the whole time is measured exactly,
    /// at the cost of two timestamps a frame, and the physics guard splits it between grid groups by
    /// their share of the work (see <see cref="PhysicsGuard"/>).
    ///
    /// This replaces sampling the third-party Profiler plugin for ten frames every thirty seconds:
    /// the load is now known at all times instead of within a sampling window, and nothing is
    /// allocated or reflected per frame.
    /// </summary>
    [PatchShim]
    public static class PhysicsLoadMonitor
    {
        /// <summary>Weight of one frame in the average: roughly the last second at 60 FPS.</summary>
        private const double Alpha = 1.0 / 60.0;

        private static readonly double TicksToMicroseconds = 1000000.0 / Stopwatch.Frequency;

        private static long _startedAt;
        private static long _averageUs;
        private static long _lastUs;
        private static long _frames;

        /// <summary>The physics update of the last second, in milliseconds of the game thread.</summary>
        public static double AverageMs => Interlocked.Read(ref _averageUs) / 1000.0;

        /// <summary>The physics update of the last frame, in milliseconds.</summary>
        public static double LastMs => Interlocked.Read(ref _lastUs) / 1000.0;

        /// <summary>Frames measured since the world was loaded; 0 means the patch never ran.</summary>
        public static long Frames => Interlocked.Read(ref _frames);

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("PhysicsLoadMonitor", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var step = typeof(MyPhysics).GetMethod(nameof(MyPhysics.Simulate),
                BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (step == null) throw new MissingMethodException("MyPhysics.Simulate");
            var self = typeof(PhysicsLoadMonitor);
            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            var pattern = ctx.GetPattern(step);
            pattern.Prefixes.Add(self.GetMethod(nameof(StepPrefix), statics));
            pattern.Suffixes.Add(self.GetMethod(nameof(StepSuffix), statics));
        }

        /// <summary>Starts over on world load, so the previous world's numbers are not carried in.</summary>
        public static void Reset()
        {
            Interlocked.Exchange(ref _averageUs, 0);
            Interlocked.Exchange(ref _lastUs, 0);
            Interlocked.Exchange(ref _frames, 0);
        }

        private static void StepPrefix() => _startedAt = Stopwatch.GetTimestamp();

        private static void StepSuffix()
        {
            var us = (long)((Stopwatch.GetTimestamp() - _startedAt) * TicksToMicroseconds);
            if (us < 0) return;
            var frames = Interlocked.Increment(ref _frames);
            // The first frame of a world seeds the average instead of dragging it up from zero.
            var average = frames == 1 ? us : (long)(_averageUs + (us - _averageUs) * Alpha);
            Interlocked.Exchange(ref _averageUs, average);
            Interlocked.Exchange(ref _lastUs, us);
        }
    }
}
