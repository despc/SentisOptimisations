using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRage.Game;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// A large grid's power distribution recomputed at most every so often while it is being built.
    ///
    /// From the server (dotTrace, 02.11.2026, a large ship welded from a projection): the game thread spent 3.2 s of
    /// 35 s - 2.7 s of it in frames over 50 ms - recomputing one grid's distribution (<c>RecomputeResourceDistribution</c>),
    /// 60 ms each time, two to three times a second. Every block welded on adds a sink and a conveyor endpoint; the
    /// conveyor graph is worked out again on a worker, and when it is done (<c>OnConveyorEndpointMappingUpdateCompleted</c>)
    /// each conveyor line asks the distributor for the power state, which recomputes everything first if anything
    /// changed. The next block makes it change again.
    ///
    /// Here a distributor's next recompute of a type waits <see cref="PauseMs"/>: <c>PauseFactor</c> times as long as its
    /// last one took, at most <see cref="MaxPauseMs"/>; one under <see cref="MinCostMs"/> does not wait at all, so
    /// ordinary grids go on as before. Asked within the pause, it is put off: the type stays marked for a recompute
    /// (its flag schedules the grid's update), the state read in the meantime is the last one worked out. The blocks
    /// welded meanwhile go in together. Not put off: a forced recompute (the game clears the force after its update)
    /// and a type the distributor has no data of yet (the game reads it by index right after).
    /// </summary>
    [PatchShim]
    public static class PowerRecomputePace
    {
        public const double MinCostMs = 1;
        public const double MaxPauseMs = 1000;

        private sealed class Pace
        {
            public long NotBefore;
        }

        private static readonly ConditionalWeakTable<MyResourceDistributorComponent, Pace> Paces = new ConditionalWeakTable<MyResourceDistributorComponent, Pace>();

        private static Func<MyResourceDistributorComponent, bool> _forced;
        private static Func<MyResourceDistributorComponent, int> _typeGroupCount;
        private static Func<MyResourceDistributorComponent, Dictionary<MyDefinitionId, int>> _typeIndex;
        private static Func<MyResourceDistributorComponent, IList> _dataPerType;
        private static Action<object> _markNeedsRecompute;

        [ThreadStatic] private static int _depth;
        [ThreadStatic] private static long _started;

        /// <summary>Since the start: recomputes run, put off, and the milliseconds the run ones took (read by the stand's scenario).</summary>
        public static long Runs, PutOffs;
        private static long _totalTicks;
        public static double TotalMs => Interlocked.Read(ref _totalTicks) * 1000.0 / Stopwatch.Frequency;

        /// <summary>A distributor counted on its own as well (the stand's scenario sets it): runs, put off, milliseconds.</summary>
        public static object Watched;
        public static long WatchedRuns, WatchedPutOffs;
        private static long _watchedTicks;
        public static double WatchedMs => Interlocked.Read(ref _watchedTicks) * 1000.0 / Stopwatch.Frequency;

        /// <summary>Recomputes put off, and the longest one run, since the last report (DiagLog, once a minute).</summary>
        private static long _putOff, _ran, _lastReport;
        private static double _longestMs;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("PowerRecomputePace", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(MyResourceDistributorComponent);
            FieldInfo F(string name) => type.GetField(name, any) ?? throw new MissingFieldException(type.Name, name);
            var arg = Expression.Parameter(type, "d");
            _forced = Expression.Lambda<Func<MyResourceDistributorComponent, bool>>(Expression.Field(arg, F("m_forceRecalculation")), arg).Compile();
            _typeGroupCount = Expression.Lambda<Func<MyResourceDistributorComponent, int>>(Expression.Field(arg, F("m_typeGroupCount")), arg).Compile();
            _typeIndex = Expression.Lambda<Func<MyResourceDistributorComponent, Dictionary<MyDefinitionId, int>>>(Expression.Field(arg, F("m_typeIdToIndex")), arg).Compile();
            var data = F("m_dataPerType");
            _dataPerType = Expression.Lambda<Func<MyResourceDistributorComponent, IList>>(Expression.Convert(Expression.Field(arg, data), typeof(IList)), arg).Compile();
            var perType = data.FieldType.GetGenericArguments()[0];
            var needs = perType.GetProperty("NeedsRecompute", any) ?? throw new MissingMemberException(perType.Name, "NeedsRecompute");
            // (its setter schedules the grid's update; a compiled call, not reflection: it runs on every put-off request)
            var item = Expression.Parameter(typeof(object), "data");
            _markNeedsRecompute = Expression.Lambda<Action<object>>(
                Expression.Call(Expression.Convert(item, perType), needs.GetSetMethod(true), Expression.Constant(true)), item).Compile();

            var recompute = type.GetMethod(nameof(MyResourceDistributorComponent.RecomputeResourceDistribution), any, null,
                                new[] { typeof(MyDefinitionId).MakeByRefType(), typeof(bool) }, null)
                            ?? throw new MissingMethodException(type.Name, "RecomputeResourceDistribution");
            var pattern = ctx.GetPattern(recompute);
            pattern.Prefixes.Add(typeof(PowerRecomputePace).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
            pattern.Suffixes.Add(typeof(PowerRecomputePace).GetMethod(nameof(Suffix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>How long a distributor waits after a recompute that took <paramref name="costMs"/>; 0: not at all.</summary>
        public static double PauseMs(double costMs, int factor) =>
            factor <= 0 || costMs < MinCostMs ? 0 : Math.Min(costMs * factor, MaxPauseMs);

        private static bool Prefix(MyResourceDistributorComponent __instance, ref MyDefinitionId typeId)
        {
            var now = Stopwatch.GetTimestamp();
            // inside a recompute (the game's own nested calls, which it turns away itself): only counted
            if (_depth > 0 && now - _started < Stopwatch.Frequency) { _depth++; return true; }
            try
            {
                var factor = SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config?.PowerRecomputePauseFactor ?? 0;
                if (factor > 0 && Paces.TryGetValue(__instance, out var pace) && now < Interlocked.Read(ref pace.NotBefore) && !_forced(__instance) &&
                    TryIndex(__instance, ref typeId, out var index))
                {
                    _markNeedsRecompute(_dataPerType(__instance)[index]);
                    Interlocked.Increment(ref _putOff);
                    Interlocked.Increment(ref PutOffs);
                    if (ReferenceEquals(__instance, Watched)) Interlocked.Increment(ref WatchedPutOffs);
                    return false;
                }
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "PowerRecomputePace: the pace check failed, recomputed now");
            }
            _depth = 1;
            _started = now;
            return true;
        }

        private static void Suffix(MyResourceDistributorComponent __instance)
        {
            if (_depth == 0) return; // put off
            if (--_depth > 0) return;
            try
            {
                var now = Stopwatch.GetTimestamp();
                var ms = (now - _started) * 1000.0 / Stopwatch.Frequency;
                Interlocked.Add(ref _totalTicks, now - _started);
                Interlocked.Increment(ref Runs);
                if (ReferenceEquals(__instance, Watched))
                {
                    Interlocked.Add(ref _watchedTicks, now - _started);
                    Interlocked.Increment(ref WatchedRuns);
                }
                var factor = SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config?.PowerRecomputePauseFactor ?? 0;
                var pause = PauseMs(ms, factor);
                if (pause > 0) Interlocked.Exchange(ref Paces.GetOrCreateValue(__instance).NotBefore, now + (long)(pause * Stopwatch.Frequency / 1000));
                else if (Paces.TryGetValue(__instance, out var pace)) Interlocked.Exchange(ref pace.NotBefore, 0);
                Interlocked.Increment(ref _ran);
                if (ms > _longestMs) _longestMs = ms;
                Report(now);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "PowerRecomputePace: timing failed");
            }
        }

        /// <summary>The type's data index, when the distributor has data of the type already.</summary>
        private static bool TryIndex(MyResourceDistributorComponent distributor, ref MyDefinitionId typeId, out int index)
        {
            index = 0;
            if (_typeGroupCount(distributor) > 1 && !_typeIndex(distributor).TryGetValue(typeId, out index)) return false;
            return index >= 0 && index < _dataPerType(distributor).Count;
        }

        private static void Report(long now)
        {
            var last = Interlocked.Read(ref _lastReport);
            if (now - last < 60 * Stopwatch.Frequency || Interlocked.CompareExchange(ref _lastReport, now, last) != last) return;
            var putOff = Interlocked.Exchange(ref _putOff, 0);
            var ran = Interlocked.Exchange(ref _ran, 0);
            var longest = _longestMs;
            _longestMs = 0;
            if (global::SentisOptimisations.DiagLog.On && putOff > 0)
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Info($"Power recomputes in the last minute: {ran} run, {putOff} put off, the longest {longest:0.0} ms");
        }
    }
}
