using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Network;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// A request to change blocks' owner that names a block the server does not have: that block left out, instead of the
    /// player being dropped.
    ///
    /// <c>MyCubeGrid.AddChangeOwnersRequestServerside</c> (the network event of a player's "Share/Owner" in the terminal)
    /// walks the requests; for a block it does not find it steps the index on and then writes <c>requests[num]</c> - past
    /// the end of the list when that block was the last. The ArgumentOutOfRangeException out of the event dropped the
    /// player (12.10.2026, the owner of a block by Cedar's flying drill changed). Here the requests for blocks the server
    /// does not have are taken out first, and their ids written to the log (at most every 10 s) - what they were is
    /// what the next look needs.
    /// </summary>
    [PatchShim]
    public static class OwnershipRequestCheck
    {
        private static DateTime _logged;

        /// <summary>Requests left out for a block the server does not have, since the start.</summary>
        public static long Dropped;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("OwnershipRequestCheck", ctx, c =>
        {
            var method = typeof(MyCubeGrid).GetMethod("AddChangeOwnersRequestServerside", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                         ?? throw new MissingMethodException("MyCubeGrid.AddChangeOwnersRequestServerside");
            // through Torch, as the others' patches of it (BlockLimiter's MyCubeGridPatch, ALE Ownership Logger's
            // BlockOwnershipTransfer on V5): a Harmony patch beside them was not run (patch_conflicts, V5 29.10.2026)
            c.GetPattern(method).Prefixes.Add(typeof(OwnershipRequestCheck).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        });

        private static void Prefix(List<MyCubeGrid.MySingleOwnershipRequest> requests)
        {
            if (requests == null || requests.Count == 0) return;
            List<long> missing = null;
            for (var i = requests.Count - 1; i >= 0; i--)
            {
                if (MyEntities.TryGetEntityById(requests[i].BlockId, out MyCubeBlock _, false)) continue;
                (missing ?? (missing = new List<long>())).Add(requests[i].BlockId);
                requests.RemoveAt(i);
            }
            if (missing == null) return;
            Dropped += missing.Count;
            if (DateTime.UtcNow - _logged < TimeSpan.FromSeconds(10)) return;
            _logged = DateTime.UtcNow;
            ulong sender = 0;
            try { sender = MyEventContext.Current.Sender.Value; } catch { /* not from the network */ }
            var who = Sync.Players?.TryGetIdentityId(sender) is long id && id != 0 ? Sync.Players.TryGetIdentity(id)?.DisplayName : sender.ToString();
            var found = string.Join(", ", missing.Select(m => MyEntities.TryGetEntityById(m, out var e, true) ? $"{m} ({e.GetType().Name}{(e.Closed ? ", closed" : "")})" : $"{m} (nothing)"));
            SentisOptimisationsPlugin.Log.Error($"OwnershipRequestCheck: {who} asked to change the owner of blocks the server does not have, left out ({Dropped} since the start): {found}");
        }
    }
}
