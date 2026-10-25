using System.Collections.Generic;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// A cap on how many projected blocks one player's welders materialize in one simulation frame.
    ///
    /// Space Engineers activates several welders in the same frame, and every projector Build is a
    /// synchronous grid mutation, so without a cap a wall of welders turns one frame into a long
    /// one. The cap is per player (the welder's owner, who the block is built for): one player's
    /// wall of welders waits its turn, another player's ship builds as fast as its own cap lets it.
    ///
    /// It is a cap and nothing more. It used to be a permit handed to one welder at a time, which
    /// went wrong twice over: a welder that never reaches the projection - and a ship carries
    /// plenty of those - held the permit and nothing was ever built; and once the permit was made
    /// to move on, it moved at the speed welders activate, a quarter of a second per welder, so a
    /// ship with twenty of them built one block every few seconds. Now any welder may build while
    /// its player's frame has room, and only one build per welder per frame, so no single tool can
    /// take the whole budget frame after frame.
    /// </summary>
    public sealed class ProjectionBuildBudget
    {
        private readonly HashSet<long> _builtThisFrame = new HashSet<long>();
        private readonly Dictionary<long, int> _usedByPlayer = new Dictionary<long, int>();
        private long _frame = long.MinValue;

        /// <summary>Whether this welder of this player may still build in this frame.</summary>
        public bool CanConsume(long frame, int limit, long welder, long player)
        {
            // welders of different clusters ask at once beside SentisClusters
            using (global::SentisOptimisations.ClusterThreads.Lock(_builtThisFrame))
            {
                BeginFrame(frame);
                if (limit < 1) limit = 1;
                return (!_usedByPlayer.TryGetValue(player, out var used) || used < limit) && !_builtThisFrame.Contains(welder);
            }
        }

        /// <summary>Takes one build out of the player's budget for the frame.</summary>
        public bool TryConsume(long frame, int limit, long welder, long player)
        {
            using (global::SentisOptimisations.ClusterThreads.Lock(_builtThisFrame))
            {
                if (!CanConsume(frame, limit, welder, player)) return false;
                _usedByPlayer.TryGetValue(player, out var used);
                _usedByPlayer[player] = used + 1;
                _builtThisFrame.Add(welder);
                return true;
            }
        }

        private void BeginFrame(long frame)
        {
            if (_frame == frame) return;
            _frame = frame;
            _usedByPlayer.Clear();
            _builtThisFrame.Clear();
        }
    }
}
