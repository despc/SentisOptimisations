using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Optimizer.Optimizations
{
    /// <summary>Game-thread, weak per-block slots. Driven every simulation frame, not by Frame10 timers.</summary>
    internal sealed class TurretSearchScheduler<T> where T : class
    {
        private ConditionalWeakTable<T, object> _registered = new ConditionalWeakTable<T, object>();
        private readonly List<WeakReference<T>>[] _slots = new List<WeakReference<T>>[TurretControlTargetSelect.Period];
        private ulong? _lastFrame;

        public TurretSearchScheduler()
        {
            for (var i = 0; i < _slots.Length; i++) _slots[i] = new List<WeakReference<T>>();
        }

        public void Register(T block, long blockId)
        {
            if (_registered.TryGetValue(block, out _)) return;
            _registered.Add(block, new object());
            _slots[(int)((ulong)blockId % (ulong)_slots.Length)].Add(new WeakReference<T>(block));
        }

        public void Tick(ulong frame, Action<T> search)
        {
            if (_lastFrame == frame) return;
            _lastFrame = frame;
            var slot = _slots[(int)(frame % (ulong)_slots.Length)];
            for (var i = slot.Count - 1; i >= 0; i--)
            {
                if (slot[i].TryGetTarget(out var block)) search(block);
                else slot.RemoveAt(i);
            }
        }

        public void Clear()
        {
            _registered = new ConditionalWeakTable<T, object>();
            foreach (var slot in _slots) slot.Clear();
            _lastFrame = null;
        }
    }
}
