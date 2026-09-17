using System.Collections.Generic;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Server-wide limit of conveyor pulls per simulation frame that serves the emptiest blocks
    /// first. The fill of the limit-th emptiest requester of the previous frame becomes the
    /// threshold for this frame; only blocks at or below it pull. Refineries run synchronised fill
    /// cycles, so without a limit whole waves of them transfer ore in the same frames. Starving
    /// blocks always pass; a hard ceiling of twice the limit bounds a frame. Game-thread state.
    /// </summary>
    public sealed class PullFrameBudget
    {
        private readonly List<float> _requestFills = new List<float>();
        private long _frame = long.MinValue;
        private int _used;
        private float _threshold = float.MaxValue;

        public bool TryAcquire(long frame, int limit, bool starving, float fill)
        {
            if (frame != _frame)
            {
                _threshold = frame == _frame + 1 ? ThresholdFor(limit) : float.MaxValue;
                _requestFills.Clear();
                _frame = frame;
                _used = 0;
            }
            _requestFills.Add(fill);
            if (!starving && (_used >= 2 * limit || fill > _threshold)) return false;
            _used++;
            return true;
        }

        private float ThresholdFor(int limit)
        {
            if (limit < 1 || _requestFills.Count <= limit) return float.MaxValue;
            _requestFills.Sort();
            return _requestFills[limit - 1];
        }
    }
}
