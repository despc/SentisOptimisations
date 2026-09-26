using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The list of online players, made once per change of it rather than once per call.
    ///
    /// <c>MyPlayerCollection.GetOnlinePlayers</c> is the <c>Values</c> of a <c>ConcurrentDictionary</c>, and that takes
    /// every lock of the dictionary and copies it into a new list, each call. The game calls it all over (sixty-odd
    /// places), several times every frame (the block limits alone once a frame), and plugins more.
    ///
    /// Here the copy is kept and handed out again (it is read-only, as before) until the players change: the three
    /// methods that add, remove and clear them bump a version, and a copy made while one of them ran carries the version
    /// from before and is made anew on the next call.
    /// </summary>
    [PatchShim]
    public static class OnlinePlayersSnapshot
    {
        private sealed class Cached
        {
            public MyPlayerCollection Owner;
            public int Version;
            public ICollection<MyPlayer> Players;
        }

        private static int _version;
        private static Cached _cached;
        private static Func<MyPlayerCollection, ConcurrentDictionary<MyPlayer.PlayerId, MyPlayer>> _players;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("OnlinePlayersSnapshot", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(MyPlayerCollection);
            var field = type.GetField("m_players", instance) ?? throw new MissingFieldException("MyPlayerCollection.m_players");
            if (field.FieldType != typeof(ConcurrentDictionary<MyPlayer.PlayerId, MyPlayer>)) throw new InvalidOperationException("MyPlayerCollection.m_players is not what OnlinePlayersSnapshot knows");
            _players = c => (ConcurrentDictionary<MyPlayer.PlayerId, MyPlayer>)field.GetValue(c);
            MethodInfo Get(string name) => type.GetMethod(name, instance | BindingFlags.DeclaredOnly) ?? throw new MissingMethodException("MyPlayerCollection." + name);
            MethodInfo Own(string name) => typeof(OnlinePlayersSnapshot).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            ctx.GetPattern(Get("GetOnlinePlayers")).Prefixes.Add(Own(nameof(GetOnlinePlayersPrefix)));
            // every change of m_players in the game goes through these three
            foreach (var name in new[] { "RemovePlayerFromDictionary", "AddPlayer", "ClearPlayers" })
                ctx.GetPattern(Get(name)).Suffixes.Add(Own(nameof(Changed)));
        }

        private static void Changed() => Interlocked.Increment(ref _version);

        private static bool GetOnlinePlayersPrefix(MyPlayerCollection __instance, ref ICollection<MyPlayer> __result)
        {
            var version = Volatile.Read(ref _version);
            var cached = Volatile.Read(ref _cached);
            if (cached != null && cached.Version == version && ReferenceEquals(cached.Owner, __instance))
            {
                __result = cached.Players;
                return false;
            }
            var players = _players(__instance).Values;
            Volatile.Write(ref _cached, new Cached { Owner = __instance, Version = version, Players = players });
            __result = players;
            return false;
        }
    }
}
