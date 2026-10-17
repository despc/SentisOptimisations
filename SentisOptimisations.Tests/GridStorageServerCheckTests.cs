using SentisOptimisationsPlugin;
using Xunit;

namespace SentisOptimisations.Tests
{
    /// <summary>How often GridStorageServerCheck tells a player that the world is being saved.</summary>
    public class GridStorageServerCheckTests
    {
        private const long Owner = 100, Mate = 200;

        [Fact]
        public void A_player_is_told_about_a_save_at_most_once_in_the_interval()
        {
            var told = new System.Collections.Generic.Dictionary<long, long>();
            Assert.True(GridStorageServerCheck.DueSavingMessage(told, Owner, 100, 50));
            Assert.False(GridStorageServerCheck.DueSavingMessage(told, Owner, 149, 50));
            Assert.True(GridStorageServerCheck.DueSavingMessage(told, Owner, 150, 50));
        }

        [Fact]
        public void Each_player_is_told_about_a_save_on_their_own()
        {
            var told = new System.Collections.Generic.Dictionary<long, long>();
            Assert.True(GridStorageServerCheck.DueSavingMessage(told, Owner, 100, 50));
            Assert.True(GridStorageServerCheck.DueSavingMessage(told, Mate, 110, 50));
            Assert.False(GridStorageServerCheck.DueSavingMessage(told, Owner, 120, 50));
        }
    }
}
