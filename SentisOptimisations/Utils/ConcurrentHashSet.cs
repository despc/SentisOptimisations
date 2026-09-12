using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SentisOptimisations.Utils;

/// <summary>
/// Thread-safe hash set: the game thread adds/removes while background loops enumerate.
/// A plain HashSet is unsafe for that pattern (corruption / InvalidOperationException).
/// </summary>
public sealed class ConcurrentHashSet<T> : IEnumerable<T>
{
    private readonly ConcurrentDictionary<T, byte> _items = new ConcurrentDictionary<T, byte>();

    public bool Add(T item) => _items.TryAdd(item, 0);

    public bool Remove(T item) => _items.TryRemove(item, out _);

    public bool Contains(T item) => _items.ContainsKey(item);

    public int Count => _items.Count;

    public void Clear() => _items.Clear();

    public IEnumerator<T> GetEnumerator() => _items.Keys.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
