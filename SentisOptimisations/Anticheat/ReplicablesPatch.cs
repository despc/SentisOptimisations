using System;
using System.Collections.Concurrent;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Replication;
using Sandbox.Game.World;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRage.Network;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// A player is not told about other players further away than the world's sync distance.
    ///
    /// The server decides for every replicable which update layer of a client it belongs to, and
    /// that decision is where the distance is applied: a character that is too far away gets no
    /// layer, so it is never replicated. Two things come out of that - the server does not send
    /// positions of players across the map, and a client cannot know about them at all, which is
    /// what a radar cheat would read.
    ///
    /// The distance is the one the server is set to run at - <c>MySession.Static.Settings.SyncDistance</c>,
    /// the same number the game replicates everything else by - so there is nothing to configure and
    /// nothing to keep in step with the world. A client always gets the character it controls, and an
    /// admin gets everyone.
    ///
    /// This runs for every replicable of every client on every frame, so it is written to cost
    /// almost nothing: the client's state is read through a bound delegate instead of
    /// <c>FieldInfo.GetValue</c>, the promote level of a client is looked up once and kept, and the
    /// distance is compared squared. The previous version asked
    /// <c>MySession.Static.Players.GetPlayerById</c> for the promote level on every single call.
    /// </summary>
    [PatchShim]
    public static class ReplicablesPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>How long a client's promote level is trusted before it is looked up again.</summary>
        private static readonly TimeSpan AdminCacheLifetime = TimeSpan.FromSeconds(30);

        /// <summary>Below this the two places overlap and the second sphere is not worth walking.</summary>
        private const double ApartToMatterM = 100;

        private static Func<object, MyClientStateBase> _clientState;
        private static Func<object, Array> _updateLayers;
        private static Func<object, MyLayers.UpdateLayerDesc> _layerDescriptor;
        private static readonly ConcurrentDictionary<ulong, (bool IsAdmin, DateTime Asked)> Admins =
            new ConcurrentDictionary<ulong, (bool, DateTime)>();

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("ReplicablesPatch", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var clientType = typeof(MyReplicationServer).Assembly.GetType("VRage.Network.MyClient");
            if (clientType == null) throw new TypeLoadException("VRage.Network.MyClient");
            _clientState = Accessors.FieldOn<MyClientStateBase>(clientType, "State");
            _updateLayers = Accessors.FieldOn<Array>(clientType, "UpdateLayers");
            var layerType = clientType.GetNestedType("UpdateLayer", BindingFlags.Public | BindingFlags.NonPublic);
            if (layerType == null) throw new TypeLoadException("MyClient.UpdateLayer");
            _layerDescriptor = Accessors.FieldOn<MyLayers.UpdateLayerDesc>(layerType, "Descriptor");

            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            var self = typeof(ReplicablesPatch);

            // MyClient has CalculateLayerOfReplicable(rep) and one that also takes a position, and
            // GetMethod by name would throw on the ambiguity, so every overload is patched.
            var layerPrefix = self.GetMethod(nameof(CalculateLayerOfReplicablePatched), statics);
            var patched = 0;
            foreach (var method in clientType.GetMethods(any | BindingFlags.DeclaredOnly))
            {
                if (method.Name != "CalculateLayerOfReplicable") continue;
                var pattern = ctx.GetPattern(method);
                pattern.Prefixes.Add(layerPrefix);
                // Only the overload that takes the second position can keep the body alive.
                if (method.GetParameters().Length > 1)
                    pattern.Suffixes.Add(self.GetMethod(nameof(KeepSecondPlaceAlive), statics));
                patched++;
            }

            if (patched == 0) throw new MissingMethodException("MyClient.CalculateLayerOfReplicable");

            var addToLayer = typeof(MyReplicationServer).GetMethod("AddReplicableToLayer",
                any | BindingFlags.DeclaredOnly);
            if (addToLayer == null) throw new MissingMethodException("MyReplicationServer.AddReplicableToLayer");
            ctx.GetPattern(addToLayer).Prefixes.Add(self.GetMethod(nameof(AddReplicableToLayerPatched), statics));
        }

        /// <summary>
        /// The layer a replicable belongs to for this client; null means it is not replicated. The
        /// result is taken away only from a character that is too far, everything else is vanilla's
        /// decision.
        /// </summary>
        private static bool CalculateLayerOfReplicablePatched(object __instance, IMyReplicable rep, ref object __result)
        {
            try
            {
                if (!ShouldHide(__instance, rep)) return true;
                __result = null;
                return false;
            }
            catch (Exception e)
            {
                Log.Error(e, "CalculateLayerOfReplicable patch failed; the layer is vanilla's");
                return true;
            }
        }

        /// <summary>
        /// The same decision where the server puts a replicable into a client's layer. __result has
        /// to be taken by reference: the previous version took it by value, so the false it assigned
        /// never left the method and this half of the patch did nothing at all.
        /// </summary>
        private static bool AddReplicableToLayerPatched(IMyReplicable rep, object client, ref bool __result)
        {
            try
            {
                if (!ShouldHide(client, rep)) return true;
                __result = false;
                return false;
            }
            catch (Exception e)
            {
                Log.Error(e, "AddReplicableToLayer patch failed; the layer is vanilla's");
                return true;
            }
        }

        /// <summary>
        /// The player's own surroundings, when they are somewhere else than what they are flying.
        /// Vanilla has already looked around the place the client is looking from; this looks around
        /// the character with the same layers, so the place the player will return to is kept as
        /// alive as the ship they are steering.
        /// </summary>
        private static void KeepSecondPlaceAlive(object __instance, IMyReplicable rep, Vector3D? secondaryPosition,
            ref object __result)
        {
            try
            {
                if (__result != null || rep == null || !secondaryPosition.HasValue) return;

                var state = _clientState(__instance);
                var primary = state?.Position;
                if (!primary.HasValue) return;

                var apart = Vector3D.DistanceSquared(primary.Value, secondaryPosition.Value);
                if (apart < ApartToMatterM * ApartToMatterM) return; // the two spheres are the same one

                var layers = _updateLayers(__instance);
                if (layers == null) return;

                var box = rep.GetAABB();
                for (var i = 0; i < layers.Length; i++)
                {
                    var layer = layers.GetValue(i);
                    if (layer == null) continue;
                    double radius = _layerDescriptor(layer).Radius;
                    var around = new BoundingBoxD(secondaryPosition.Value - radius, secondaryPosition.Value + radius);
                    if (!around.Intersects(box)) continue;
                    __result = layer;
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Keeping the player's own surroundings alive failed");
            }
        }

        /// <summary>
        /// Whether this client must not be told about this replicable: another player's character,
        /// further away than the world's sync distance, and the client is not an admin.
        /// </summary>
        private static bool ShouldHide(object client, IMyReplicable rep)
        {
            var distance = MySession.Static?.Settings?.SyncDistance ?? 0;
            if (distance <= 0) return false;
            if (!(rep is MyEntityReplicableBaseEvent<MyCharacter> characterReplicable)) return false;

            var character = characterReplicable.Instance;
            if (character?.PositionComp == null) return false;

            var state = _clientState(client);
            if (state == null) return false;

            // Without a position there is nothing to measure against, so the character is left to
            // vanilla rather than hidden - a client that has just joined has no position yet.
            if (!state.Position.HasValue) return false;

            var steamId = state.EndpointId.Id.Value;
            if (steamId == character.ControlSteamId) return false; // his own character
            if (IsAdmin(steamId)) return false;

            var apart = Vector3D.DistanceSquared(state.Position.Value, character.PositionComp.GetPosition());
            return apart > (double)distance * distance;
        }

        /// <summary>
        /// The client's promote level, kept for <see cref="AdminCacheLifetime"/>. It is read from the
        /// player list, which is a dictionary lookup with an allocation, and this is asked for every
        /// replicable of every client on every frame.
        /// </summary>
        private static bool IsAdmin(ulong steamId)
        {
            var now = DateTime.UtcNow;
            if (Admins.TryGetValue(steamId, out var known) && now - known.Asked < AdminCacheLifetime)
            {
                return known.IsAdmin;
            }

            var isAdmin = PlayerUtils.IsAdmin(PlayerUtils.GetPlayer(steamId));
            Admins[steamId] = (isAdmin, now);
            return isAdmin;
        }

        /// <summary>Forgets a client that left.</summary>
        public static void Forget(ulong steamId) => Admins.TryRemove(steamId, out _);

        public static void ClearAll() => Admins.Clear();
    }
}
