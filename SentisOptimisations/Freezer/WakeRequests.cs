using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Sandbox;
using Sandbox.Game.Entities;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin.Freezer
{
    /// <summary>
    /// Grids that something needs awake for a while, even though no player is standing next to them.
    ///
    /// A laser antenna is the case this was written for: connecting one is a conversation between two
    /// blocks, and a frozen grid does not update, so its dish never turns and the link never forms.
    /// The end that is awake - the one beside the player - asks for the other end to be kept awake
    /// while it is reaching for it.
    ///
    /// The request expires by itself, so nothing has to remember to cancel it, and it only ever comes
    /// from a grid that is awake in the first place: two frozen grids linked to each other keep each
    /// other asleep, and a chain of links across the map does not wake the whole map.
    /// </summary>
    public static class WakeRequests
    {
        private static readonly ConcurrentDictionary<long, DateTime> Until = new ConcurrentDictionary<long, DateTime>();

        /// <summary>Keeps the entity's grid awake for the next <paramref name="seconds"/>.</summary>
        public static void Keep(MyEntity entity, double seconds)
        {
            var grid = (entity?.GetTopMostParent() ?? entity) as MyCubeGrid;
            if (grid == null || grid.MarkedForClose) return;
            var until = DateTime.UtcNow.AddSeconds(seconds);
            Until.AddOrUpdate(grid.EntityId, until, (_, known) => known > until ? known : until);
        }

        /// <summary>Whether any grid of the group is being kept awake right now.</summary>
        public static bool IsAwake(IEnumerable<MyCubeGrid> grids)
        {
            if (Until.IsEmpty || grids == null) return false;
            var now = DateTime.UtcNow;
            foreach (var grid in grids)
            {
                if (grid == null) continue;
                if (!Until.TryGetValue(grid.EntityId, out var until)) continue;
                if (until > now) return true;
                Until.TryRemove(grid.EntityId, out _);
            }

            return false;
        }

        /// <summary>How many grids are being held awake; for the statistics line.</summary>
        public static int Count
        {
            get
            {
                var now = DateTime.UtcNow;
                var count = 0;
                foreach (var pair in Until)
                {
                    if (pair.Value > now) count++;
                    else Until.TryRemove(pair.Key, out _);
                }

                return count;
            }
        }

        public static void ClearAll() => Until.Clear();
    }
}
