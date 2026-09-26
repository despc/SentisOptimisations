using System;
using System.Reflection;
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
    /// The grid storage of the Services Terminal checks a grid on the server when the grid is sent, not only before.
    ///
    /// The client asks the server to check a grid first (<c>ValidateGridEndpoint</c>: the whole <c>ValidateGrid</c> -
    /// the grid is the sender's, within the limits, not static, its inventories empty where items are not allowed,
    /// the sender's storage not full) and then sends the store request with the grid's id. The server ran that second
    /// request (<c>StoreGridTrusted</c>) with only part of the check. Here the same <c>ValidateGrid</c> runs again
    /// at that point; a grid that does not pass is not stored, and the sender gets the reason back as the game
    /// sends it for any refused request. What passes goes on to the game's own handling unchanged.
    ///
    /// The requests about a grid already in storage name its record by id, and the server did not match the record
    /// with the sender: fetching it back (which makes the sender its owner), its check, deleting it, sharing it. Here
    /// they follow the rule the game itself uses to list the records to a player: fetched back by its owner, or by a
    /// member of the owner's faction when the owner shared it; deleted and shared only by its owner. A request that
    /// does not match is dropped (a fetch is answered "not found", the same as for a record that is not there).
    /// </summary>
    [PatchShim]
    public static class GridStorageServerCheck
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static MethodInfo _validate;
        private static FieldInfo _session;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("GridStorageServerCheck", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(MyGridsStorageEntityComponent);
            var store = type.GetMethod("StoreGridTrusted", instance, null, new[] { typeof(long) }, null)
                        ?? throw new MissingMethodException("MyGridsStorageEntityComponent.StoreGridTrusted");
            _validate = type.GetMethod("ValidateGrid", BindingFlags.Static | BindingFlags.NonPublic)
                        ?? throw new MissingMethodException("MyGridsStorageEntityComponent.ValidateGrid");
            _session = type.GetField("m_sessionComponent", instance)
                       ?? throw new MissingFieldException("MyGridsStorageEntityComponent.m_sessionComponent");
            if (_validate.ReturnType != typeof(MyGridStorageRequestResult) || _validate.GetParameters().Length != 4)
                throw new InvalidOperationException("MyGridsStorageEntityComponent.ValidateGrid is not what GridStorageServerCheck knows");
            ctx.GetPattern(store).Prefixes.Add(typeof(GridStorageServerCheck).GetMethod(nameof(StorePrefix), BindingFlags.Static | BindingFlags.NonPublic));

            MethodInfo Handler(string name, params Type[] args) =>
                type.GetMethod(name, instance, null, args, null) ?? throw new MissingMethodException("MyGridsStorageEntityComponent." + name);
            MethodInfo Own(string name) => typeof(GridStorageServerCheck).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            ctx.GetPattern(Handler("RetrieveGridTrusted", typeof(Guid), typeof(bool))).Prefixes.Add(Own(nameof(RetrievePrefix)));
            ctx.GetPattern(Handler("ValidateRetrievalEndpoint", typeof(Guid))).Prefixes.Add(Own(nameof(RetrievalCheckPrefix)));
            ctx.GetPattern(Handler("DeleteGridEndpoint", typeof(Guid))).Prefixes.Add(Own(nameof(OwnerOnlyPrefix)));
            ctx.GetPattern(Handler("SetOwnershipKindEndpoint", typeof(Guid), typeof(Sandbox.ModAPI.MyStoredGridOwnershipKind))).Prefixes.Add(Own(nameof(OwnerOnlyPrefix)));
        }

        /// <summary>The game's own check of a grid for storage, as the terminal would run it for this identity.</summary>
        public static MyGridStorageRequestResult Check(MyGridsStorageEntityComponent terminal, MyCubeGrid grid, MyIdentity identity)
        {
            var terminalGrid = (terminal.Entity as MyCubeBlock)?.CubeGrid;
            return (MyGridStorageRequestResult)_validate.Invoke(null, new[] { grid, terminalGrid, identity, _session.GetValue(terminal) });
        }

        /// <summary>Whether a player may fetch a stored record back: theirs, or shared with their faction by its owner.</summary>
        public static bool MayFetch(MyStoredGridData record, long identityId) =>
            MayFetch(record.OwnerId, record.OwnershipKind == Sandbox.Common.ObjectBuilders.MyStoredGridOwnershipKind.FactionShare, identityId,
                (owner, player) =>
                {
                    var faction = MySession.Static.Factions.GetPlayerFaction(owner);
                    return faction != null && faction.IsMember(player);
                });

        /// <summary>
        /// The rule itself: the owner may; anyone else only when the owner shared the record with the faction and they
        /// are in the owner's faction (<paramref name="sameFaction"/>(owner, player)). Nobody may fetch a record of no one.
        /// </summary>
        public static bool MayFetch(long ownerId, bool sharedWithFaction, long identityId, Func<long, long, bool> sameFaction)
        {
            if (identityId == 0) return false;
            if (ownerId == identityId) return true;
            return sharedWithFaction && ownerId != 0 && sameFaction(ownerId, identityId);
        }

        /// <summary>Whether a player may delete a stored record or change who it is shared with: its owner only.</summary>
        public static bool MayChange(long ownerId, long identityId) => identityId != 0 && ownerId == identityId;

        /// <summary>The record and the sender's identity; false when either is not there (the game handles that itself).</summary>
        private static bool Context(MyGridsStorageEntityComponent terminal, Guid id, out MyStoredGridData record, out long identityId, out ulong sender)
        {
            record = null;
            identityId = 0;
            sender = 0;
            var endpoint = MyEventContext.Current.Sender;
            if (!endpoint.IsValid) return false;
            sender = endpoint.Value;
            identityId = MySession.Static.Players.TryGetIdentityId(sender);
            var session = _session.GetValue(terminal) as SpaceEngineers.Game.SessionComponents.MyGridsStorageSessionComponent;
            return identityId != 0 && session != null && session.GetStoredGridsData().TryGetValue(id, out record);
        }

        private static bool RetrievePrefix(MyGridsStorageEntityComponent __instance, Guid id)
        {
            try
            {
                if (!Context(__instance, id, out var record, out var identityId, out var sender) || MayFetch(record, identityId)) return true;
                Log.Warn($"Grid storage: record {id} ('{record.SavedGridDetails?.DisplayName}') of {record.OwnerId} not handed to {identityId} ({sender})");
                __instance.OnGridStorageRetrievalRequestFinished(MyGridStorageRequestResult.GridNotFound, sender);
                return false;
            }
            catch (Exception e)
            {
                Log.Error(e, "Grid storage: the fetch check failed; the request is refused");
                return false;
            }
        }

        private static bool RetrievalCheckPrefix(MyGridsStorageEntityComponent __instance, Guid id)
        {
            try
            {
                return !Context(__instance, id, out var record, out var identityId, out _) || MayFetch(record, identityId);
            }
            catch (Exception e)
            {
                Log.Error(e, "Grid storage: the fetch check failed; the request is refused");
                return false;
            }
        }

        private static bool OwnerOnlyPrefix(MyGridsStorageEntityComponent __instance, Guid id)
        {
            try
            {
                if (!Context(__instance, id, out var record, out var identityId, out var sender) || MayChange(record.OwnerId, identityId)) return true;
                Log.Warn($"Grid storage: record {id} ('{record.SavedGridDetails?.DisplayName}') of {record.OwnerId} not changed for {identityId} ({sender}): not its owner");
                return false;
            }
            catch (Exception e)
            {
                Log.Error(e, "Grid storage: the owner check failed; the request is refused");
                return false;
            }
        }

        private static bool StorePrefix(MyGridsStorageEntityComponent __instance, long entityId)
        {
            try
            {
                var sender = MyEventContext.Current.Sender;
                if (!sender.IsValid) return true;
                var identity = MySession.Static.Players.TryGetIdentity(MySession.Static.Players.TryGetIdentityId(sender.Value));
                if (identity == null) return true;          // the game refuses it itself
                var grid = MyEntities.GetEntityById(entityId) as MyCubeGrid;
                var result = Check(__instance, grid, identity);
                if (result == MyGridStorageRequestResult.Success) return true;
                Log.Warn($"Grid storage: '{grid?.DisplayName}' ({entityId}) not stored for '{identity.DisplayName}' ({sender.Value}): {result}");
                __instance.OnGridStorageDepositRequestFinished(result, sender.Value);
                return false;
            }
            catch (Exception e)
            {
                // no store on a failed check: the request is refused, the game goes on
                Log.Error(e, "Grid storage: the check failed; the request is refused");
                return false;
            }
        }
    }
}
