using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using SentisOptimisations.DelayedLogic;
using SentisOptimisationsPlugin;
using Xunit;

namespace SentisOptimisations.Tests
{
    /// <summary>
    /// KNOWN DEFECT BOARD. Each test here encodes the CORRECT behavior for a defect confirmed
    /// in the code review; they are RED until the corresponding fix lands. Keep one test per
    /// finding; delete/rename into the green suite as fixes are verified.
    /// </summary>
    public class KnownDefectTests
    {
        // The AsyncWeld finding was fixed by deleting the flag entirely (weld is always
        // game-thread since AsyncWeld v2); the defect test retired with it. See
        // AsyncWeldV2Tests.AsyncWeld_setting_is_removed.

        // ----------------------------------------------------- finding: searchlight prefix misbinding
        [Fact]
        public void Searchlight_prefix_must_take_a_searchlight_instance()
        {
            var row = AllBindings.Rows.SingleOrDefault(r =>
                r.Original.DeclaringType?.Name == "MySearchlight" &&
                r.Original.Name == "UpdateAfterSimulation");
            Assert.NotNull(row.Original);
            var inst = row.Patch.GetParameters().FirstOrDefault(p => p.Name == "__instance");
            if (inst != null)
                Assert.True(inst.ParameterType.IsAssignableFrom(row.Original.DeclaringType),
                    $"prefix {row.Patch.Name} declares __instance {inst.ParameterType.Name}, " +
                    "but the target is MySearchlight → InvalidProgram the first time a searchlight updates");
        }

        // ------------------------------------------------- finding: DelayedProcessor cross-thread race
        [Fact]
        public void DelayedProcessor_survives_concurrent_adds_while_loop_runs()
        {
            var p = new DelayedProcessor();
            p.OnLoaded();
            using var capture = new NLogCapture();
            try
            {
                const int threads = 8, perThread = 150;
                var hits = new int[threads * perThread];
                var adders = Enumerable.Range(0, threads).Select(t => new Thread(() =>
                {
                    var rnd = new Random(t);
                    for (int i = 0; i < perThread; i++)
                    {
                        int idx = t * perThread + i;
                        p.AddDelayedAction(DateTime.Now.AddMilliseconds(rnd.Next(0, 400)),
                            () => Interlocked.Increment(ref hits[idx]));
                    }
                })).ToList();
                adders.ForEach(t => t.Start());
                adders.ForEach(t => t.Join());

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 20000 && hits.Sum() < hits.Length) Thread.Sleep(20);

                Assert.True(hits.Sum() == hits.Length,
                    $"lost/duplicated actions: {hits.Sum()}/{hits.Length} executed");
                Assert.All(hits, h => Assert.Equal(1, h));
                Assert.DoesNotContain(capture.Errors, e => e.Contains("DelayedLogic Error"));
            }
            finally { p.OnUnloading(); }
        }

        // --------------------------------- finding: throwing delayed action is retried forever
        [Fact]
        public void Throwing_delayed_action_must_not_be_retried_forever()
        {
            var p = new DelayedProcessor();
            p.OnLoaded();
            try
            {
                int calls = 0;
                p.AddDelayedAction(DateTime.Now, () =>
                {
                    Interlocked.Increment(ref calls);
                    throw new Exception("bad action");
                });
                Thread.Sleep(3500); // ~7 loop passes
                Assert.True(Volatile.Read(ref calls) <= 2,
                    $"throwing action re-ran {calls} times: it must be dropped after failing");
            }
            finally { p.OnUnloading(); }
        }

        // -------------------------------------- finding: SendReplicablesAsync swallows all errors
        [Fact]
        public void SendReplicables_errors_must_be_logged()
        {
            lock (SendReplicablesAsync._queue) SendReplicablesAsync._queue.Clear();
            // reset the throttle so a previous test's logged error doesn't suppress this one
            typeof(SendReplicablesAsync)
                .GetField("_lastErrorLogged", BindingFlags.Static | BindingFlags.NonPublic)
                ?.SetValue(null, DateTime.MinValue);
            var s = new SendReplicablesAsync();
            s.OnLoaded();
            using var capture = new NLogCapture();
            try
            {
                SendReplicablesAsync._queue.Enqueue(new ExplodingWrapper());
                Thread.Sleep(1500);
                Assert.True(capture.Errors.Any(),
                    "DoSendToClient failures are silently swallowed — a broken replication wrapper is invisible");
            }
            finally { s.OnUnloading(); lock (SendReplicablesAsync._queue) SendReplicablesAsync._queue.Clear(); }
        }

        sealed class ExplodingWrapper : AsyncSync.ISendToClientWrapper
        {
            public void DoSendToClient() => throw new Exception("simulated network failure");
        }

        // --------------------------------- finding: entity-keyed batch state is never cleaned up
        [Fact]
        public void GasTank_batch_state_must_shrink_with_the_world()
        {
            var dictField = typeof(GasTankOptimisations)
                .GetField("_accumulatedTransfer", BindingFlags.Static | BindingFlags.NonPublic);
            var state = dictField.GetValue(null);
            var countProp = state.GetType().GetProperty("Count");
            // structural requirement: some cleanup hook must exist that empties per-entity state
            var cleanup = typeof(GasTankOptimisations).GetMethods(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(m => m.Name.IndexOf("Cleanup", StringComparison.OrdinalIgnoreCase) >= 0
                       || m.Name.IndexOf("OnEntityRemove", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.True(cleanup,
                "GasTankOptimisations keeps per-tank state forever; needs an entity-removal cleanup hook");
        }

        // ------------------------------- finding: EntitiesObserver exposes plain collections to loops
        [Fact]
        public void Observer_collections_must_be_thread_safe()
        {
            var offenders = new List<string>();
            foreach (var asm in PluginHarness.PluginAssemblies)
            foreach (var t in asm.GetTypes().Where(t => t.Name.Contains("Observer")))
            foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Instance |
                                          BindingFlags.NonPublic | BindingFlags.Public))
            {
                var ft = f.FieldType;
                if (ft.IsGenericType)
                {
                    var def = ft.GetGenericTypeDefinition();
                    if (def.Name.StartsWith("HashSet") || def.Name.StartsWith("List") || def.Name.StartsWith("Dictionary"))
                        offenders.Add($"{t.FullName}.{f.Name} : {ft.Name}");
                }
            }
            Assert.True(offenders.Count == 0,
                "game-thread-written collections iterated by background loops:\n" + string.Join("\n", offenders));
        }

        // ---------------------------- finding: GasTank flush drops the current cycle's transfer
        [Fact]
        public void GasTank_flush_must_not_lose_the_current_transfer()
        {
            PluginHarness.SetConfig("GasTankOptimisation", true);
            var m = typeof(GasTankOptimisations).GetMethod("MethodExecuteGasTransferPatched",
                BindingFlags.Static | BindingFlags.NonPublic);
            var tank = System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(Sandbox.Game.Entities.Blocks.MyGasTank));

            // accumulate 30 calls of 1.0 (ref args come back in the object[] on reflection calls)
            for (int i = 0; i < 30; i++)
            {
                object[] a = { tank, 1.0 };
                var skip = (bool)m.Invoke(null, a);
                Assert.False(skip);
            }
            // 31st call: original transfer (1.0) plus the 30 accumulated must be flushed
            object[] flush = { tank, 1.0 };
            var run = (bool)m.Invoke(null, flush);
            Assert.True(run);
            // current behavior assigns the sum of the 30 (30.0) and DROPS the 31st unit (1.0)
            Assert.True(Math.Abs((double)flush[1] - 31.0) < 1e-6, "flush loses the current call's gas transfer");
        }
    }
}
