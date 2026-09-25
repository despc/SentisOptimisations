using System.Diagnostics;
using System.Linq;
using System.Threading;
using SentisOptimisationsPlugin;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class EntityInitDrainTests
    {
        [Fact]
        public void Returns_at_once_when_nothing_is_outstanding()
        {
            EntityInitDrain.WaitUntilZero(() => 0);
        }

        [Fact]
        public void Ends_with_the_work_not_at_the_next_timer_tick()
        {
            // work of ~1 ms on another thread; SpinWait.SpinUntil would often wake 15.6 ms later
            var waits = Enumerable.Range(0, 10).Select(_ =>
            {
                var outstanding = 1;
                var worker = new Thread(() =>
                {
                    var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000;
                    while (Stopwatch.GetTimestamp() < until) { }
                    Interlocked.Decrement(ref outstanding);
                });
                var watch = Stopwatch.StartNew();
                worker.Start();
                EntityInitDrain.WaitUntilZero(() => Volatile.Read(ref outstanding));
                worker.Join();
                return watch.Elapsed.TotalMilliseconds;
            }).OrderBy(ms => ms).ToList();
            Assert.True(waits[5] < 8, "median wait " + waits[5] + " ms");
        }
    }
}
