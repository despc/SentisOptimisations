using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SpaceEngineers.Game.EntityComponents.Blocks;
using Torch.Managers.PatchManager;
using Sandbox.Common.ObjectBuilders;
using VRage.Network;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The grid storage of the Services Terminal stores no grid during a save of the world
    /// (<see cref="GridStorageSaveWindow"/>): a grid stored then loses its files.
    ///
    /// The client's check of a grid (<c>ValidateGrid</c>) answers <c>UnspecifiedStorageIssue</c> then, so the player is
    /// told before being asked to drop the inventory; a store request that comes anyway (<c>StoreGridTrusted</c>) is
    /// refused before anything else (nothing dropped, nothing queued). Either way the player gets a private chat message
    /// to try again later, at most once in <see cref="SavingMessageEvery"/>.
    /// </summary>
    [PatchShim]
    public static class GridStorageServerCheck
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>What the player is told when a store request comes during a save of the world.</summary>
        public const string SavingMessage = "The world is being saved, try again later.";

        /// <summary>The least time between two <see cref="SavingMessage"/>s to one player (one goes on every click of "Store" otherwise).</summary>
        public static readonly TimeSpan SavingMessageEvery = TimeSpan.FromSeconds(5);

        private static readonly Harmony Harmony = new Harmony("SentisOptimisations.GridStorageServerCheck");
        private static readonly Dictionary<long, long> SavingTold = new Dictionary<long, long>();

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("GridStorageServerCheck", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(MyGridsStorageEntityComponent);
            var store = type.GetMethod("StoreGridTrusted", instance, null, new[] { typeof(long) }, null)
                        ?? throw new MissingMethodException("MyGridsStorageEntityComponent.StoreGridTrusted");
            var validate = type.GetMethod("ValidateGrid", BindingFlags.Static | BindingFlags.NonPublic)
                           ?? throw new MissingMethodException("MyGridsStorageEntityComponent.ValidateGrid");
            if (validate.ReturnType != typeof(MyGridStorageRequestResult) || validate.GetParameters().Length != 4)
                throw new InvalidOperationException("MyGridsStorageEntityComponent.ValidateGrid is not what GridStorageServerCheck knows");
            MethodInfo Own(string name) => typeof(GridStorageServerCheck).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            ctx.GetPattern(store).Prefixes.Add(Own(nameof(StorePrefix)));
            // By Harmony, not Torch: a Torch suffix with a ref __result of a struct threw in the patched method itself
            // (AsteroidNearReplication, 10.10.2026).
            try
            {
                Harmony.Patch(validate, postfix: new HarmonyMethod(Own(nameof(ValidateSuffix))));
            }
            catch (Exception e)
            {
                Log.Error(e, "Grid storage: the save check of the client's grid check is not patched; a store during a save is still refused");
            }
        }

        /// <summary>Whether the game's check lets a grid be stored: on success, and on <c>InventoryIssue</c> alone (the game drops the items).</summary>
        public static bool WouldStore(MyGridStorageRequestResult result) =>
            result == MyGridStorageRequestResult.Success || result == MyGridStorageRequestResult.InventoryIssue;

        /// <summary>
        /// The client's check of a grid during a save: a grid that would be stored is refused, so the player is not asked
        /// to drop the inventory for nothing; a grid refused for its own reason keeps that reason.
        /// </summary>
        private static void ValidateSuffix(MyCubeGrid grid, MyIdentity callerIdentity, ref MyGridStorageRequestResult __result)
        {
            try
            {
                if (!WouldStore(__result) || !GridStorageSaveWindow.Saving) return;
                __result = MyGridStorageRequestResult.UnspecifiedStorageIssue;
                Log.Info($"Grid storage: '{grid?.DisplayName}' ({grid?.EntityId}) not stored for '{callerIdentity?.DisplayName}' ({(callerIdentity == null ? 0 : MySession.Static.Players.TryGetSteamId(callerIdentity.IdentityId))}): the world is being saved (check)");
                if (callerIdentity != null) TellSaving(callerIdentity.IdentityId);
            }
            catch (Exception e)
            {
                Log.Error(e, "Grid storage: the save check of a grid failed; the game's answer is kept");
            }
        }

        /// <summary>Whether a player is told about a save now: not told in the last <c>everyTicks</c> (as of <c>now</c>); a yes is noted.</summary>
        public static bool DueSavingMessage(Dictionary<long, long> told, long identityId, long now, long everyTicks)
        {
            if (told.TryGetValue(identityId, out var last) && now - last < everyTicks) return false;
            told[identityId] = now;
            return true;
        }

        internal static void TellSaving(long identityId)
        {
            bool due;
            lock (SavingTold)
                due = DueSavingMessage(SavingTold, identityId, Stopwatch.GetTimestamp(), (long)(SavingMessageEvery.TotalSeconds * Stopwatch.Frequency));
            if (due) global::SentisOptimisations.ChatUtils.SendTo(identityId, SavingMessage, "Grid storage");
        }

        private static bool StorePrefix(MyGridsStorageEntityComponent __instance, long entityId)
        {
            try
            {
                if (!GridStorageSaveWindow.Saving) return true;
                var sender = MyEventContext.Current.Sender;
                if (!sender.IsValid) return true;
                var identity = MySession.Static.Players.TryGetIdentity(MySession.Static.Players.TryGetIdentityId(sender.Value));
                if (identity == null) return true;          // the game refuses it itself
                var grid = MyEntities.GetEntityById(entityId) as MyCubeGrid;
                // a grid stored during a save loses its files (GridStorageSaveWindow); nothing is dropped or queued
                Log.Info($"Grid storage: '{grid?.DisplayName}' ({entityId}) not stored for '{identity.DisplayName}' ({sender.Value}): the world is being saved");
                __instance.OnGridStorageDepositRequestFinished(MyGridStorageRequestResult.UnspecifiedStorageIssue, sender.Value);
                TellSaving(identity.IdentityId);
                return false;
            }
            catch (Exception e)
            {
                Log.Error(e, "Grid storage: the save check failed; the request goes on");
                return true;
            }
        }
    }
}
