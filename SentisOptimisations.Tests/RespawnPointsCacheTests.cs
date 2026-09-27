using System;
using SentisOptimisationsPlugin;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class RespawnPointsCacheTests
    {
        private static readonly DateTime Tested = new DateTime(2026, 9, 27, 9, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void A_fresh_answer_for_a_point_in_place_is_used()
        {
            Assert.True(RespawnPointsCache.Usable(Tested, Tested.AddSeconds(30), 0.2));
        }

        [Fact]
        public void An_old_answer_is_not_used()
        {
            Assert.False(RespawnPointsCache.Usable(Tested, Tested.AddSeconds(61), 0));
        }

        [Fact]
        public void An_answer_for_a_point_that_moved_is_not_used()
        {
            Assert.False(RespawnPointsCache.Usable(Tested, Tested.AddSeconds(5), 3));
        }
    }
}
