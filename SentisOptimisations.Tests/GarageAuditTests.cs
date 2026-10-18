using System;
using SentisOptimisations.Commands;
using Xunit;

namespace SentisOptimisations.Tests
{
    /// <summary>The rules of the !garage commands: which records are dead, the days away, names and file names.</summary>
    public class GarageAuditTests
    {
        [Fact]
        public void A_record_of_a_living_owner_is_alive_however_it_is_shared()
        {
            Assert.Equal(GarageOwnerState.Alive, GarageAudit.OwnerState(true, false, false));
            Assert.Equal(GarageOwnerState.Alive, GarageAudit.OwnerState(true, true, true));
        }

        [Fact]
        public void A_private_record_of_a_removed_owner_is_dead()
        {
            Assert.Equal(GarageOwnerState.Deleted, GarageAudit.OwnerState(false, false, true));
        }

        [Fact]
        public void A_shared_record_of_a_removed_owner_stays_while_the_faction_has_a_member()
        {
            Assert.Equal(GarageOwnerState.DeletedSharedWithFaction, GarageAudit.OwnerState(false, true, true));
        }

        [Fact]
        public void A_shared_record_of_a_removed_owner_with_no_faction_left_is_dead()
        {
            Assert.Equal(GarageOwnerState.Deleted, GarageAudit.OwnerState(false, true, false));
        }

        [Fact]
        public void Days_away_are_whole_days_and_unknown_without_a_date()
        {
            var now = new DateTime(2026, 10, 17, 12, 0, 0);
            Assert.Equal(14, GarageAudit.DaysSince(now.AddDays(-14.5), now));
            Assert.Equal(0, GarageAudit.DaysSince(now.AddHours(-3), now));
            Assert.Equal(0, GarageAudit.DaysSince(now.AddHours(3), now));
            Assert.Null(GarageAudit.DaysSince(null, now));
            Assert.Null(GarageAudit.DaysSince(DateTime.MinValue, now));
        }

        [Fact]
        public void A_Steam_id_is_told_from_an_identity_id()
        {
            Assert.True(GarageAudit.IsSteamId(76561199026338700UL));
            Assert.True(GarageAudit.IsSteamId(76561198030545669UL));
            Assert.False(GarageAudit.IsSteamId(144115188075856838UL));
            Assert.False(GarageAudit.IsSteamId(42UL));
        }

        [Fact]
        public void A_name_matches_a_part_of_it_ignoring_case()
        {
            Assert.True(GarageAudit.NameMatches("[GLE]Ice", "ice"));
            Assert.True(GarageAudit.NameMatches("Лом Кор М водород", "кор м"));
            Assert.False(GarageAudit.NameMatches("Склад", "бак"));
            Assert.False(GarageAudit.NameMatches(null, "бак"));
            Assert.False(GarageAudit.NameMatches("Склад", " "));
        }

        [Fact]
        public void Every_file_of_a_stored_grid_gives_its_id()
        {
            var id = Guid.Parse("06ee3eb7-9e9f-458c-be20-46f739d6a79a");
            foreach (var name in new[] { $"gs-{id}.sbc", $"gs-{id}.sbcB5", $"gs-{id}-detail.sbc" })
            {
                Assert.True(GarageAudit.TryParseFileId(name, out var parsed));
                Assert.Equal(id, parsed);
            }
        }

        [Fact]
        public void Other_files_give_no_id()
        {
            Assert.False(GarageAudit.TryParseFileId("Sandbox.sbc", out _));
            Assert.False(GarageAudit.TryParseFileId("gs-short.sbc", out _));
            Assert.False(GarageAudit.TryParseFileId(null, out _));
        }

        [Fact]
        public void Sizes_read_in_the_unit_that_fits()
        {
            Assert.Equal("512 КБ", GarageAudit.Size(512 * 1024));
            Assert.Equal("1.5 МБ", GarageAudit.Size(1536 * 1024));
            Assert.Equal("2 ГБ", GarageAudit.Size(2L * 1024 * 1024 * 1024));
        }
    }
}
