using System;
using System.Collections.Generic;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Produces a bounded, resumable slice of indices for a projected-grid scan. A call never
    /// returns more than one full projection, even when the configured budget is larger.
    /// </summary>
    public sealed class ProjectionFrontierCursor
    {
        private int _count = -1;
        private int _next;

        public IEnumerable<int> Take(int count, int budget)
        {
            if (count <= 0 || budget <= 0)
            {
                _count = count;
                _next = 0;
                yield break;
            }

            if (_count != count)
            {
                _count = count;
                _next = 0;
            }

            var take = Math.Min(count, budget);
            for (var i = 0; i < take; i++)
            {
                yield return _next;
                _next++;
                if (_next >= count) _next = 0;
            }
        }

        public void Reset()
        {
            _count = -1;
            _next = 0;
        }
    }
}
