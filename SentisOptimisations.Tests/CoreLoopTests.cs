using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SentisOptimisations.DelayedLogic;
using SentisOptimisationsPlugin;
using SentisOptimisationsPlugin.ShipTool;
using Xunit;
using Xunit.Abstractions;

namespace SentisOptimisations.Tests
{
    public class DelayedProcessorTests
    {
        readonly ITestOutputHelper _out;
        public DelayedProcessorTests(ITestOutputHelper output) { _out = output; }

        static bool WaitUntil(Func<bool> cond, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
                if (cond()) return true;
            Thread.Sleep(5);
            return cond();
        }

        [Fact]
        public void Action_runs_after_due_time_and_only_once()
        {
            var p = new DelayedProcessor();
            p.OnLoaded();
            try
            {
                int count = 0;
                p.AddDelayedAction(DateTime.Now.AddMilliseconds(300), () => Interlocked.Increment(ref count));
                Thread.Sleep(100);
                Assert.Equal(0, count); // not before due
                Assert.True(WaitUntil(() => Volatile.Read(ref count) >= 1, 5000), "action never ran");
                Thread.Sleep(1200);
                Assert.Equal(1, count); // exactly once
            }
            finally { p.OnUnloading(); }
        }

        [Fact]
        public void Same_due_time_does_not_drop_actions()
        {
            var p = new DelayedProcessor();
            p.OnLoaded();
            try
            {
                var t = DateTime.Now.AddMilliseconds(200);
                int count = 0;
                for (int i = 0; i < 25; i++)
                    p.AddDelayedAction(t, () => Interlocked.Increment(ref count));
                Assert.True(WaitUntil(() => Volatile.Read(ref count) >= 25, 8000), $"only {count}/25 ran");
                Assert.Equal(25, count);
            }
            finally { p.OnUnloading(); }
        }

        [Fact]
        public void No_actions_run_after_unloading()
        {
            var p = new DelayedProcessor();
            p.OnLoaded();
            p.OnUnloading();
            Thread.Sleep(800); // let the loop observe cancellation and exit (it sleeps 500 ms per pass)
            int count = 0;
            p.AddDelayedAction(DateTime.Now, () => Interlocked.Increment(ref count));
            Thread.Sleep(1500);
            Assert.Equal(0, count);
        }
    }

    public class ShipToolsAsyncQueuesTests
    {
        [Fact]
        public void Executes_all_enqueued_in_fifo_order()
        {
            var q = new ShipToolsAsyncQueues();
            q.OnLoaded();
            try
            {
                var order = new List<int>();
                for (int i = 0; i < 20; i++)
                {
                    int captured = i;
                    q.EnqueueAction(() => { lock (order) order.Add(captured); });
                }
                Assert.True(TestSync.WaitUntil(() => { lock (order) return order.Count == 20; }, 8000),
                    $"only {order.Count}/20 ran");
                lock (order) Assert.Equal(Enumerable.Range(0, 20), order);
            }
            finally { q.OnUnloading(); }
        }

        [Fact]
        public void Throwing_action_does_not_kill_the_loop()
        {
            var q = new ShipToolsAsyncQueues();
            q.OnLoaded();
            try
            {
                int done = 0;
                q.EnqueueAction(() => throw new InvalidOperationException("boom"));
                q.EnqueueAction(() => Interlocked.Increment(ref done));
                Assert.True(TestSync.WaitUntil(() => Volatile.Read(ref done) == 1, 8000), "loop died on throwing action");
            }
            finally { q.OnUnloading(); }
        }

        [Fact]
        public void Concurrent_enqueue_loses_nothing()
        {
            var q = new ShipToolsAsyncQueues();
            q.OnLoaded();
            try
            {
                int done = 0;
                Parallel.For(0, 8, t =>
                {
                    for (int i = 0; i < 100; i++)
                        q.EnqueueAction(() => Interlocked.Increment(ref done));
                });
                Assert.True(TestSync.WaitUntil(() => Volatile.Read(ref done) == 800, 15000), $"done={done}");
                Assert.Equal(800, done);
            }
            finally { q.OnUnloading(); }
        }
    }

    public class SendReplicablesAsyncTests
    {
        sealed class Wrapper : AsyncSync.ISendToClientWrapper
        {
            readonly Action _a;
            public Wrapper(Action a) { _a = a; }
            public void DoSendToClient() => _a();
        }

        static void ClearQueue()
        {
            lock (SendReplicablesAsync._queue) SendReplicablesAsync._queue.Clear();
        }

        [Fact]
        public void Sends_in_order_and_survives_exceptions()
        {
            ClearQueue();
            var s = new SendReplicablesAsync();
            s.OnLoaded();
            try
            {
                var order = new List<int>();
                for (int i = 0; i < 10; i++)
                {
                    int c = i;
                    SendReplicablesAsync._queue.Enqueue(new Wrapper(() => { lock (order) order.Add(c); }));
                }
                SendReplicablesAsync._queue.Enqueue(new Wrapper(() => throw new Exception("network boom")));
                int after = 0;
                SendReplicablesAsync._queue.Enqueue(new Wrapper(() => Interlocked.Increment(ref after)));

                Assert.True(TestSync.WaitUntil(() => Volatile.Read(ref after) == 1, 8000), "loop died on throwing wrapper");
                lock (order) Assert.Equal(Enumerable.Range(0, 10), order);
            }
            finally { s.OnUnloading(); ClearQueue(); }
        }
    }

    static class TestSync
    {
        public static bool WaitUntil(Func<bool> cond, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (cond()) return true;
                Thread.Sleep(5);
            }
            return cond();
        }
    }
}
