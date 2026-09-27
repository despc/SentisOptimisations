using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using Sandbox.Definitions;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The respawn screen's list of respawn points answered from what was worked out a little earlier.
    ///
    /// A player in the respawn screen asks for the list every 10 seconds (<c>MyGuiScreenMedicals.RefreshRespawnPointsRequest</c>),
    /// and for every respawn point the server tests with the physics whether a character would fit there
    /// (<c>MyPlayer.CanSpawnAt</c>: up to three <c>MyEntities.FindFreePlace</c>) - 7-19 ms of one frame for ten points on
    /// the stand, per player in that screen. Whether a point has room does not depend on who asks: here, while the list is
    /// being made, the answer for a point is the one worked out at most <see cref="MaxAge"/> ago; the points asked about
    /// lately are worked out again one at a time, a few frames apart (<see cref="Tick"/>), with the game's own test. The
    /// respawn itself tests the room again as before - the list only shows what is likely.
    /// </summary>
    [PatchShim]
    public static class RespawnPointsCache
    {
        /// <summary>An answer older than this is not used.</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);
        /// <summary>An answer older than this is worked out again in the background (if the point was asked about lately).</summary>
        public static readonly TimeSpan RefreshAfter = TimeSpan.FromSeconds(20);
        /// <summary>A point not asked about for this long is forgotten.</summary>
        public static readonly TimeSpan ForgetAfter = TimeSpan.FromSeconds(90);
        /// <summary>Frames between two background tests.</summary>
        public const int FramesBetweenTests = 30;

        private sealed class Entry
        {
            public bool CanSpawn;
            public DateTime Tested, Asked;
            public MatrixD Matrix;
            public Vector3 Velocity;
            public MyEntity SpawnedBy;
            public MyPlayer Player;
            public string Model;
        }

        // game thread only
        private static readonly Dictionary<long, Entry> Entries = new Dictionary<long, Entry>();
        [ThreadStatic] private static bool _inList, _testing;
        private static MethodInfo _canSpawnAt;
        private static int _frame;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("RespawnPointsCache", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var screen = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SpaceEngineers.Game.GUI.MyGuiScreenMedicals", false)).FirstOrDefault(t => t != null)
                         ?? throw new TypeLoadException("SpaceEngineers.Game.GUI.MyGuiScreenMedicals");
            var request = screen.GetMethod("RefreshRespawnPointsRequest", any, null, Type.EmptyTypes, null)
                          ?? throw new MissingMethodException("MyGuiScreenMedicals.RefreshRespawnPointsRequest");
            _canSpawnAt = typeof(MyPlayer).GetMethod("CanSpawnAt", any) ?? throw new MissingMethodException("MyPlayer.CanSpawnAt");
            MethodInfo Own(string name) => typeof(RespawnPointsCache).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            ctx.GetPattern(request).Prefixes.Add(Own(nameof(ListPrefix)));
            ctx.GetPattern(request).Suffixes.Add(Own(nameof(ListSuffix)));
            ctx.GetPattern(_canSpawnAt).Prefixes.Add(Own(nameof(CanSpawnAtPrefix)));
            ctx.GetPattern(_canSpawnAt).Suffixes.Add(Own(nameof(CanSpawnAtSuffix)));
        }

        /// <summary>Whether an answer tested then may be used now for a point at the same place.</summary>
        public static bool Usable(DateTime tested, DateTime now, double moved) => now - tested <= MaxAge && moved < 1;

        private static void ListPrefix() => _inList = true;
        private static void ListSuffix() => _inList = false;

        private static bool CanSpawnAtPrefix(MyPlayer __instance, MatrixD worldMatrix, Vector3 velocity, MyEntity spawnedBy, MyBotDefinition botDefinition,
            string modelName, ref bool __result)
        {
            if (!_inList || _testing || spawnedBy == null || botDefinition != null) return true;
            var now = DateTime.UtcNow;
            if (!Entries.TryGetValue(spawnedBy.EntityId, out var entry)) return true;
            entry.Asked = now;
            entry.Player = __instance;
            if (!Usable(entry.Tested, now, Vector3D.Distance(entry.Matrix.Translation, worldMatrix.Translation))) return true;
            __result = entry.CanSpawn;
            return false;
        }

        private static void CanSpawnAtSuffix(MyPlayer __instance, MatrixD worldMatrix, Vector3 velocity, MyEntity spawnedBy, MyBotDefinition botDefinition,
            string modelName, bool __result)
        {
            if (!_inList || _testing || spawnedBy == null || botDefinition != null) return;
            var now = DateTime.UtcNow;
            if (Entries.TryGetValue(spawnedBy.EntityId, out var known) &&
                Usable(known.Tested, now, Vector3D.Distance(known.Matrix.Translation, worldMatrix.Translation))) return;   // answered from here
            Entries[spawnedBy.EntityId] = new Entry
            {
                CanSpawn = __result, Tested = now, Asked = now, Matrix = worldMatrix, Velocity = velocity,
                SpawnedBy = spawnedBy, Player = __instance, Model = modelName,
            };
        }

        /// <summary>Game thread, every frame: a point asked about lately and tested a while ago is tested again, one at a time.</summary>
        public static void Tick()
        {
            if (Entries.Count == 0 || ++_frame % FramesBetweenTests != 0) return;
            var now = DateTime.UtcNow;
            foreach (var gone in Entries.Where(e => now - e.Value.Asked > ForgetAfter || e.Value.SpawnedBy.MarkedForClose).Select(e => e.Key).ToList())
                Entries.Remove(gone);
            var due = Entries.Values.Where(e => now - e.Tested > RefreshAfter).OrderBy(e => e.Tested).FirstOrDefault();
            if (due == null || due.Player?.Identity == null) return;
            // (a point that moved since - on a ship - is not answered from here anyway: the list tests it the game's way)
            var matrix = due.Matrix;
            _testing = true;
            try
            {
                due.CanSpawn = (bool)_canSpawnAt.Invoke(due.Player, new object[] { matrix, due.Velocity, due.SpawnedBy, null, due.Model });
                due.Tested = now;
            }
            catch (Exception)
            {
                Entries.Remove(due.SpawnedBy.EntityId);
            }
            finally
            {
                _testing = false;
            }
        }
    }
}
