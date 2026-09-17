using System;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Exponential backoff for a block's conveyor pulls that keep finding nothing. After
    /// <see cref="EmptyPullsBeforeBackoff"/> empty pulls in a row the next attempts are skipped for
    /// 2, 4, 8... pull periods, capped; any successful pull resets it. The grace period matters:
    /// vanilla PullItems also returns nothing while conveyor paths are still being computed in the
    /// background (right after a grid is loaded), which is not an empty network.
    /// A backoff is only valid for the network state it observed: callers pass a generation that
    /// changes when new items can have appeared (ore stored somewhere, conveyor graph rebuilt), and
    /// a changed generation cancels the wait immediately.
    /// Game-thread state, one instance per block.
    /// </summary>
    public sealed class PullBackoff
    {
        public const int EmptyPullsBeforeBackoff = 3;

        private long _skipUntilFrame = long.MinValue;
        private int _emptyPulls;
        private long _waitFrames;
        private long _generation;

        public bool ShouldSkip(long frame, long maxBackoffFrames, long generation)
        {
            if (generation != _generation)
            {
                Reset();
                _generation = generation;
                return false;
            }
            return maxBackoffFrames > 0 && frame < _skipUntilFrame;
        }

        /// <param name="jitter">0..1, stable per block. Scales each wait into [50%, 100%] so blocks
        /// woken by the same event do not rescan their network in the same frames forever after.</param>
        public void OnResult(long frame, bool pulledAnything, long stepFrames, long maxBackoffFrames, long generation,
            double jitter = 1.0)
        {
            if (generation != _generation)
            {
                Reset();
                _generation = generation;
            }
            if (pulledAnything || maxBackoffFrames <= 0)
            {
                Reset();
                return;
            }
            if (++_emptyPulls < EmptyPullsBeforeBackoff) return;

            _waitFrames = _waitFrames == 0 ? 2 * stepFrames : Math.Min(_waitFrames * 2, maxBackoffFrames);
            _waitFrames = Math.Min(_waitFrames, maxBackoffFrames);
            var jitterFactor = 0.5 + 0.5 * Math.Max(0.0, Math.Min(1.0, jitter));
            _skipUntilFrame = frame + Math.Max(1, (long)(_waitFrames * jitterFactor));
        }

        private void Reset()
        {
            _emptyPulls = 0;
            _waitFrames = 0;
            _skipUntilFrame = long.MinValue;
        }
    }
}
