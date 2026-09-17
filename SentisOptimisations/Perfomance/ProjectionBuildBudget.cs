using System.Collections.Generic;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Global fair game-thread budget for projection materialization. Space Engineers invokes
    /// several welders in the same simulation frame; this prevents their Build calls from
    /// accumulating into one long frame while rotating the permit between active welders.
    /// </summary>
    public sealed class ProjectionBuildBudget
    {
        private const long StaleFrames = 120;
        private readonly LinkedList<long> _owners = new LinkedList<long>();
        private readonly Dictionary<long, long> _lastSeen = new Dictionary<long, long>();
        private long _frame = long.MinValue;
        private int _used;

        public bool CanConsume(long frame, int limit, long owner)
        {
            BeginCall(frame, owner);
            if (limit < 1) limit = 1;
            return _used < limit && _owners.First != null && _owners.First.Value == owner;
        }

        public bool TryConsume(long frame, int limit, long owner)
        {
            if (!CanConsume(frame, limit, owner)) return false;
            _used++;
            var turn = _owners.First;
            _owners.RemoveFirst();
            _owners.AddLast(turn);
            return true;
        }

        private void BeginCall(long frame, long owner)
        {
            if (_frame != frame)
            {
                _frame = frame;
                _used = 0;
            }

            var added = !_lastSeen.ContainsKey(owner);
            if (added)
                _owners.AddLast(owner);
            _lastSeen[owner] = frame;

            // A contender may first become visible after this frame's permit was consumed.
            // Move the consumer behind it so the newcomer owns the next available turn.
            if (added && _used > 0 && _owners.Count > 1)
            {
                var previous = _owners.First;
                _owners.RemoveFirst();
                _owners.AddLast(previous);
            }

            var node = _owners.First;
            while (node != null)
            {
                var next = node.Next;
                long seen;
                if (!_lastSeen.TryGetValue(node.Value, out seen) || frame - seen > StaleFrames)
                {
                    _lastSeen.Remove(node.Value);
                    _owners.Remove(node);
                }
                node = next;
            }
        }
    }
}
