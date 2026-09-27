using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using Sandbox.Definitions;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Network;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The respawn screen's list of respawn points answered from what was worked out a little earlier, and within a few
    /// milliseconds when nothing was.
    ///
    /// A player in the respawn screen asks for the list every 10 seconds (<c>MyGuiScreenMedicals.RefreshRespawnPointsRequest</c>),
    /// and for every respawn point the server tests with the physics whether a character would fit there
    /// (<c>MyPlayer.CanSpawnAt</c>: up to three <c>MyEntities.FindFreePlace</c>) - 7-29 ms of one frame for ten points on
    /// the stand, per player in that screen. Whether a point has room does not depend on who asks: here, while the list is
    /// being made, the answer for a point is the one worked out at most <see cref="MaxAge"/> ago; the points asked about
    /// lately are worked out again one at a time, a few frames apart (<see cref="Tick"/>), with the game's own test.
    ///
    /// A point not worked out yet is tested while the list has used less than <see cref="ListBudgetMs"/>; after that it is
    /// shown as having room, and tested in the frames that follow, one a frame. Should one of them turn out to have none,
    /// the player is sent the list again at once. The respawn itself tests the room again as before - the list only
    /// shows what is likely.
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
        /// <summary>Frames between two background tests of points already worked out.</summary>
        public const int FramesBetweenTests = 30;
        /// <summary>The testing one list may do before the points left are shown as having room and tested later.</summary>
        public const double ListBudgetMs = 3;

        private sealed class Entry
        {
            public bool CanSpawn, Guessed;
            public DateTime Tested, Asked;
            public MatrixD Matrix;
            public Vector3 Velocity;
            public MyEntity SpawnedBy;
            public MyPlayer Player;
            public string Model;
        }

        // game thread only
        private static readonly Dictionary<long, Entry> Entries = new Dictionary<long, Entry>();
        /// <summary>The players shown a guess, and the points guessed for them.</summary>
        private static readonly Dictionary<ulong, HashSet<long>> Guessed = new Dictionary<ulong, HashSet<long>>();
        [ThreadStatic] private static bool _inList, _testing;
        private static long _listStarted;
        private static MethodInfo _canSpawnAt, _request;
        private static int _frame;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("RespawnPointsCache", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var screen = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SpaceEngineers.Game.GUI.MyGuiScreenMedicals", false)).FirstOrDefault(t => t != null)
                         ?? throw new TypeLoadException("SpaceEngineers.Game.GUI.MyGuiScreenMedicals");
            _request = screen.GetMethod("RefreshRespawnPointsRequest", any, null, Type.EmptyTypes, null)
                       ?? throw new MissingMethodException("MyGuiScreenMedicals.RefreshRespawnPointsRequest");
            _canSpawnAt = typeof(MyPlayer).GetMethod("CanSpawnAt", any) ?? throw new MissingMethodException("MyPlayer.CanSpawnAt");
            MethodInfo Own(string name) => typeof(RespawnPointsCache).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            ctx.GetPattern(_request).Prefixes.Add(Own(nameof(ListPrefix)));
            ctx.GetPattern(_request).Suffixes.Add(Own(nameof(ListSuffix)));
            ctx.GetPattern(_canSpawnAt).Prefixes.Add(Own(nameof(CanSpawnAtPrefix)));
            ctx.GetPattern(_canSpawnAt).Suffixes.Add(Own(nameof(CanSpawnAtSuffix)));
        }

        /// <summary>Whether an answer tested then may be used now for a point at the same place.</summary>
        public static bool Usable(DateTime tested, DateTime now, double moved) => now - tested <= MaxAge && moved < 1;

        /// <summary>Whether a list that has tested for so long may test one more point itself.</summary>
        public static bool MayTest(long elapsedTicks, long frequency) => elapsedTicks * 1000.0 / frequency < ListBudgetMs;

        private static void ListPrefix()
        {
            _inList = true;
            _listStarted = Stopwatch.GetTimestamp();
        }

        private static void ListSuffix() => _inList = false;

        private static bool CanSpawnAtPrefix(MyPlayer __instance, MatrixD worldMatrix, Vector3 velocity, MyEntity spawnedBy, MyBotDefinition botDefinition,
            string modelName, ref bool __result)
        {
            if (!_inList || _testing || spawnedBy == null || botDefinition != null) return true;
            var now = DateTime.UtcNow;
            if (Entries.TryGetValue(spawnedBy.EntityId, out var entry))
            {
                entry.Asked = now;
                entry.Player = __instance;
                if (Usable(entry.Tested, now, Vector3D.Distance(entry.Matrix.Translation, worldMatrix.Translation)))
                {
                    __result = entry.CanSpawn;
                    return false;
                }
            }
            if (MayTest(Stopwatch.GetTimestamp() - _listStarted, Stopwatch.Frequency)) return true;
            // no time left in this list: shown as having room, tested in the next frames (see Tick)
            var sender = MyEventContext.Current.Sender.Value;
            Entries[spawnedBy.EntityId] = new Entry
            {
                CanSpawn = true, Guessed = true, Tested = DateTime.MinValue, Asked = now, Matrix = worldMatrix, Velocity = velocity,
                SpawnedBy = spawnedBy, Player = __instance, Model = modelName,
            };
            if (sender != 0)
            {
                if (!Guessed.TryGetValue(sender, out var points)) Guessed[sender] = points = new HashSet<long>();
                points.Add(spawnedBy.EntityId);
            }
            __result = true;
            return false;
        }

        private static void CanSpawnAtSuffix(MyPlayer __instance, MatrixD worldMatrix, Vector3 velocity, MyEntity spawnedBy, MyBotDefinition botDefinition,
            string modelName, bool __result)
        {
            if (!_inList || _testing || spawnedBy == null || botDefinition != null) return;
            var now = DateTime.UtcNow;
            if (Entries.TryGetValue(spawnedBy.EntityId, out var known) &&
                (known.Guessed || Usable(known.Tested, now, Vector3D.Distance(known.Matrix.Translation, worldMatrix.Translation)))) return;   // answered from here
            Entries[spawnedBy.EntityId] = new Entry
            {
                CanSpawn = __result, Tested = now, Asked = now, Matrix = worldMatrix, Velocity = velocity,
                SpawnedBy = spawnedBy, Player = __instance, Model = modelName,
            };
        }

        /// <summary>
        /// Game thread, every frame: a point shown as having room without a test is tested (one a frame); a point asked
        /// about lately and tested a while ago is tested again (one every <see cref="FramesBetweenTests"/> frames); a
        /// player shown a guess that was wrong gets the list again.
        /// </summary>
        public static void Tick()
        {
            if (Entries.Count == 0) return;
            _frame++;
            var guessed = Entries.Values.FirstOrDefault(e => e.Guessed);
            if (guessed != null)
            {
                Test(guessed);
                if (!Entries.Values.Any(e => e.Guessed)) ResendWhereWrong();
                return;
            }
            if (_frame % FramesBetweenTests != 0) return;
            var now = DateTime.UtcNow;
            foreach (var gone in Entries.Where(e => now - e.Value.Asked > ForgetAfter || e.Value.SpawnedBy.MarkedForClose).Select(e => e.Key).ToList())
                Entries.Remove(gone);
            var due = Entries.Values.Where(e => now - e.Tested > RefreshAfter).OrderBy(e => e.Tested).FirstOrDefault();
            if (due != null) Test(due);
        }

        /// <summary>The game's own test of one point.</summary>
        private static void Test(Entry entry)
        {
            entry.Guessed = false;
            if (entry.Player?.Identity == null || entry.SpawnedBy.MarkedForClose)
            {
                Entries.Remove(entry.SpawnedBy.EntityId);
                return;
            }
            // (a point that moved since - on a ship - is not answered from here anyway: the list tests it the game's way)
            _testing = true;
            try
            {
                entry.CanSpawn = (bool)_canSpawnAt.Invoke(entry.Player, new object[] { entry.Matrix, entry.Velocity, entry.SpawnedBy, null, entry.Model });
                entry.Tested = DateTime.UtcNow;
            }
            catch (Exception)
            {
                Entries.Remove(entry.SpawnedBy.EntityId);
            }
            finally
            {
                _testing = false;
            }
        }

        /// <summary>The players shown a point as having room that has none: the list again (everything is known now).</summary>
        private static void ResendWhereWrong()
        {
            foreach (var pair in Guessed.ToList())
            {
                var wrong = pair.Value.Any(id => Entries.TryGetValue(id, out var e) && !e.CanSpawn);
                Guessed.Remove(pair.Key);
                if (!wrong) continue;
                try
                {
                    using (MyEventContext.Set(new EndpointId(pair.Key), null, false))
                        _request.Invoke(null, null);
                }
                catch (Exception e)
                {
                    SentisOptimisationsPlugin.Log.Warn(e, "RespawnPointsCache: the corrected list could not be sent");
                }
            }
        }
    }
}
