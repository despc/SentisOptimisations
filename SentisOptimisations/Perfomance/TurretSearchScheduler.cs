using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Weak per-block slots, driven every simulation frame, not by Frame10 timers. Registered from the turrets'
    /// timers, which with SentisClusters run in the clusters' threads side by side, and ticked on the game thread:
    /// the slots are kept under a lock, the searches run outside it.
    /// </summary>
    internal sealed class TurretSearchScheduler<T> where T : class
    {
        private readonly object _gate = new object();
        private ConditionalWeakTable<T, object> _registered = new ConditionalWeakTable<T, object>();
        private readonly List<WeakReference<T>>[] _slots = new List<WeakReference<T>>[TurretControlTargetSelect.Period];
        private readonly List<T> _due = new List<T>();
        private ulong? _lastFrame;

        public TurretSearchScheduler()
        {
            for (var i = 0; i < _slots.Length; i++) _slots[i] = new List<WeakReference<T>>();
        }

        public void Register(T block, long blockId)
        {
            lock (_gate)
            {
                if (_registered.TryGetValue(block, out _)) return;
                _registered.Add(block, new object());
                _slots[(int)((ulong)blockId % (ulong)_slots.Length)].Add(new WeakReference<T>(block));
            }
        }

        public void Tick(ulong frame, Action<T> search)
        {
            lock (_gate)
            {
                if (_lastFrame == frame) return;
                _lastFrame = frame;
                _due.Clear();
                var slot = _slots[(int)(frame % (ulong)_slots.Length)];
                for (var i = slot.Count - 1; i >= 0; i--)
                {
                    if (slot[i].TryGetTarget(out var block)) _due.Add(block);
                    else slot.RemoveAt(i);
                }
            }
            try
            {
                foreach (var block in _due) search(block);
            }
            finally
            {
                _due.Clear();
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _registered = new ConditionalWeakTable<T, object>();
                foreach (var slot in _slots) slot.Clear();
                _lastFrame = null;
            }
        }
    }
}
