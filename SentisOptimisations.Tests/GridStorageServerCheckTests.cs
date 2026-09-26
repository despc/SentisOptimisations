using SentisOptimisationsPlugin;
using Xunit;

namespace SentisOptimisations.Tests
{
    /// <summary>The access rule of GridStorageServerCheck for records in the grid storage.</summary>
    public class GridStorageServerCheckTests
    {
        private const long Owner = 100, Mate = 200, Stranger = 300;

        // Owner and Mate are in one faction; Stranger is in none of theirs
        private static bool SameFaction(long a, long b) => (a == Owner || a == Mate) && (b == Owner || b == Mate);

        [Fact]
        public void The_owner_fetches_their_own_record()
        {
            Assert.True(GridStorageServerCheck.MayFetch(Owner, false, Owner, SameFaction));
            Assert.True(GridStorageServerCheck.MayFetch(Owner, true, Owner, SameFaction));
        }

        [Fact]
        public void A_faction_mate_fetches_a_record_shared_with_the_faction()
        {
            Assert.True(GridStorageServerCheck.MayFetch(Owner, true, Mate, SameFaction));
        }

        [Fact]
        public void A_faction_mate_does_not_fetch_a_record_kept_private()
        {
            Assert.False(GridStorageServerCheck.MayFetch(Owner, false, Mate, SameFaction));
        }

        [Fact]
        public void A_player_outside_the_faction_fetches_no_record_of_the_owner()
        {
            Assert.False(GridStorageServerCheck.MayFetch(Owner, false, Stranger, SameFaction));
            Assert.False(GridStorageServerCheck.MayFetch(Owner, true, Stranger, SameFaction));
        }

        [Fact]
        public void No_identity_and_no_owner_fetch_nothing()
        {
            Assert.False(GridStorageServerCheck.MayFetch(Owner, true, 0, (a, b) => true));
            Assert.False(GridStorageServerCheck.MayFetch(0, true, Mate, (a, b) => true));
        }

        [Fact]
        public void The_faction_is_asked_about_the_owner_and_the_player()
        {
            long askedOwner = 0, askedPlayer = 0;
            GridStorageServerCheck.MayFetch(Owner, true, Mate, (a, b) => { askedOwner = a; askedPlayer = b; return true; });
            Assert.Equal(Owner, askedOwner);
            Assert.Equal(Mate, askedPlayer);
        }

        [Fact]
        public void Only_the_owner_deletes_or_reshares_a_record()
        {
            Assert.True(GridStorageServerCheck.MayChange(Owner, Owner));
            Assert.False(GridStorageServerCheck.MayChange(Owner, Mate));
            Assert.False(GridStorageServerCheck.MayChange(Owner, Stranger));
            Assert.False(GridStorageServerCheck.MayChange(0, 0));
        }
    }
}
