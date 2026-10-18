using System;
using System.Globalization;

namespace SentisOptimisations.Commands
{
    /// <summary>Who a record of the grid storage still belongs to, for the purge of records of removed players.</summary>
    public enum GarageOwnerState
    {
        /// <summary>The owner's identity is in the world.</summary>
        Alive,

        /// <summary>The owner's identity is gone and nobody else may fetch the grid: the record is dead.</summary>
        Deleted,

        /// <summary>
        /// The owner's identity is gone, but the record is shared with the owner's faction and the faction still has a
        /// member: the game hands such a grid to the faction members, so it is not dead.
        /// </summary>
        DeletedSharedWithFaction
    }

    /// <summary>The rules of the garage commands that need no game: kept apart so they are tested.</summary>
    public static class GarageAudit
    {
        public static GarageOwnerState OwnerState(bool ownerExists, bool sharedWithFaction, bool factionHasMember)
        {
            if (ownerExists) return GarageOwnerState.Alive;
            return sharedWithFaction && factionHasMember ? GarageOwnerState.DeletedSharedWithFaction : GarageOwnerState.Deleted;
        }

        /// <summary>Whole days from <paramref name="last"/> to <paramref name="now"/>; null when it is not known.</summary>
        public static int? DaysSince(DateTime? last, DateTime now)
        {
            if (!last.HasValue || last.Value == DateTime.MinValue) return null;
            return last.Value >= now ? 0 : (int)(now - last.Value).TotalDays;
        }

        /// <summary>
        /// Whether a number is a Steam id of an individual account (7656119xxxxxxxxxx) and not an identity id of the game
        /// (about 1.44e17, larger than any Steam id).
        /// </summary>
        public static bool IsSteamId(ulong value) => value >= 76561197960265728UL && value < 76561202255233024UL;

        /// <summary>A grid name contains the query, ignoring case.</summary>
        public static bool NameMatches(string name, string query) =>
            !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(query) &&
            name.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;

        public static string Size(long bytes)
        {
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " КБ";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " МБ";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " ГБ";
        }

        /// <summary>The id of a stored grid from the name of one of its files (<c>gs-&lt;id&gt;.sbc</c>, <c>.sbcB5</c>, <c>-detail.sbc</c>).</summary>
        public static bool TryParseFileId(string fileName, out Guid id)
        {
            id = Guid.Empty;
            const string prefix = "gs-";
            const int guidLength = 36;
            return fileName != null && fileName.StartsWith(prefix, StringComparison.Ordinal) &&
                   fileName.Length >= prefix.Length + guidLength &&
                   Guid.TryParse(fileName.Substring(prefix.Length, guidLength), out id);
        }
    }
}
