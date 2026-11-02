using System;
using System.Collections.Generic;

namespace SentisOptimisationsPlugin.Freezer;

/// <summary>
/// Work for the game thread, run a frame at a time within a budget of milliseconds, in the order it came. One item a key:
/// a key queued again while it waits is merged into the waiting item (keeps its place). Queued from any thread.
///
/// Made for the assemblers' conveyor pulls: every assembler's timer fired on the same tick, and their pulls all ran in
/// one frame through InvokeOnGameThread - up to 12 ms, 306 of their 397 ms in frames over 16.7 ms (prod, dotTrace,
/// 02.11.2026).
/// </summary>
public sealed class FrameBudgetQueue<T>
{
    private readonly object _lock = new object();
    private readonly Queue<long> _order = new Queue<long>();
    private readonly Dictionary<long, T> _items = new Dictionary<long, T>();

    /// <summary>Queues the item; true when the key was not waiting yet.</summary>
    public bool Enqueue(long key, T item, Func<T, T, T> merge)
    {
        lock (_lock)
        {
            if (_items.TryGetValue(key, out var waiting))
            {
                _items[key] = merge(waiting, item);
                return false;
            }
            _items[key] = item;
            _order.Enqueue(key);
            return true;
        }
    }

    public int Count
    {
        get { lock (_lock) return _order.Count; }
    }

    /// <summary>
    /// Runs waiting items in order until <paramref name="elapsedMs"/> reaches the budget, at least one a call (so the
    /// queue always moves); the items run.
    /// </summary>
    public int Drain(double budgetMs, Func<double> elapsedMs, Action<T> run)
    {
        var done = 0;
        while (true)
        {
            T item;
            lock (_lock)
            {
                if (_order.Count == 0) break;
                var key = _order.Dequeue();
                item = _items[key];
                _items.Remove(key);
            }
            run(item);
            done++;
            if (elapsedMs() >= budgetMs) break;
        }
        return done;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _order.Clear();
            _items.Clear();
        }
    }
}
