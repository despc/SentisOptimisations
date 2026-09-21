using System.Collections.Generic;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// A cap on how many projected blocks are materialized in one simulation frame.
    ///
    /// Space Engineers activates several welders in the same frame, and every projector Build is a
    /// synchronous grid mutation, so without a cap a wall of welders turns one frame into a long
    /// one. The cap is global, because that is what the frame feels.
    ///
    /// It is a cap and nothing more. It used to be a permit handed to one welder at a time, which
    /// went wrong twice over: a welder that never reaches the projection - and a ship carries
    /// plenty of those - held the permit and nothing was ever built; and once the permit was made
    /// to move on, it moved at the speed welders activate, a quarter of a second per welder, so a
    /// ship with twenty of them built one block every few seconds. Now any welder may build while
    /// the frame has room, and only one build per welder per frame, so no single tool can take the
    /// whole budget frame after frame.
    /// </summary>
    public sealed class ProjectionBuildBudget
    {
        private readonly HashSet<long> _builtThisFrame = new HashSet<long>();
        private long _frame = long.MinValue;
        private int _used;

        /// <summary>Whether this welder may still build in this frame.</summary>
        public bool CanConsume(long frame, int limit, long owner)
        {
            BeginFrame(frame);
            if (limit < 1) limit = 1;
            return _used < limit && !_builtThisFrame.Contains(owner);
        }

        /// <summary>Takes one build out of the frame's budget.</summary>
        public bool TryConsume(long frame, int limit, long owner)
        {
            if (!CanConsume(frame, limit, owner)) return false;
            _used++;
            _builtThisFrame.Add(owner);
            return true;
        }

        private void BeginFrame(long frame)
        {
            if (_frame == frame) return;
            _frame = frame;
            _used = 0;
            _builtThisFrame.Clear();
        }
    }
}
