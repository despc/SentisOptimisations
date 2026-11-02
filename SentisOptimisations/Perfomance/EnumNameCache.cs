using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using Torch.Managers.PatchManager;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// The name of an enum value worked out once, not on every <c>ToString</c>.
    ///
    /// On .NET Framework every <c>Enum.ToString()</c> - and every enum in <c>$"{...}"</c> or <c>string.Format</c> -
    /// goes to <c>Enum.InternalFormat</c>, which asks by reflection whether the type is <c>[Flags]</c>
    /// (<c>CustomAttribute.IsDefined</c>) and takes the names and values from the type's cache, which the runtime holds
    /// weakly: a collection drops it and the next call builds it again. On the server (dotTrace 02.11.2026 17:42) a
    /// player's script printing its state spent 135 ms of 35 s there, in one frame 7.6 ms when the cache was rebuilt.
    ///
    /// The text is a pure function of the type and the value, so it is kept: the first call of a value runs the
    /// game's own path and its text is remembered, the next ones take it. At most <see cref="PerType"/> values of a
    /// type and <see cref="Types"/> types are kept, so values made up at run time (flags combinations, numbers with no
    /// name) cannot grow it without end; past that the runtime's own path runs as before.
    /// </summary>
    [PatchShim]
    public static class EnumNameCache
    {
        public const int PerType = 512;
        public const int Types = 4096;

        private sealed class Names
        {
            public readonly ConcurrentDictionary<object, string> ByValue = new ConcurrentDictionary<object, string>();
        }

        private static readonly ConcurrentDictionary<Type, Names> Cache = new ConcurrentDictionary<Type, Names>();

        /// <summary>Texts taken from the cache and worked out by the runtime, since the start.</summary>
        public static long Hits, Misses;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("EnumNameCache", ctx, PatchImpl);

        public static MethodInfo Target()
        {
            var runtimeType = typeof(Type).Assembly.GetType("System.RuntimeType", true);
            return typeof(Enum).GetMethod("InternalFormat", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { runtimeType, typeof(object) }, null)
                   ?? throw new MissingMethodException("System.Enum", "InternalFormat");
        }

        internal static void PatchImpl(PatchContext ctx)
        {
            var pattern = ctx.GetPattern(Target());
            pattern.Prefixes.Add(typeof(EnumNameCache).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
            pattern.Suffixes.Add(typeof(EnumNameCache).GetMethod(nameof(Suffix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>The kept text of <paramref name="value"/> (the enum's underlying value, boxed) of the enum type <paramref name="type"/>.</summary>
        public static bool TryGet(Type type, object value, out string text)
        {
            text = null;
            return type != null && value != null && Cache.TryGetValue(type, out var names) && names.ByValue.TryGetValue(value, out text);
        }

        /// <summary>Keeps the text the runtime worked out, while there is room.</summary>
        public static void Keep(Type type, object value, string text)
        {
            if (type == null || value == null || text == null) return;
            if (!Cache.TryGetValue(type, out var names))
            {
                if (Cache.Count >= Types) return;
                names = Cache.GetOrAdd(type, _ => new Names());
            }
            if (names.ByValue.Count >= PerType) return;
            names.ByValue.TryAdd(value, text);
        }

        // whether this thread's call was answered from the cache (the suffix runs either way)
        [ThreadStatic] private static bool _hit;

        private static bool Prefix(Type eT, object value, ref string __result)
        {
            _hit = TryGet(eT, value, out var text);
            if (!_hit) return true;
            __result = text;
            Interlocked.Increment(ref Hits);
            return false;
        }

        private static void Suffix(Type eT, object value, string __result)
        {
            if (_hit) { _hit = false; return; }
            Interlocked.Increment(ref Misses);
            Keep(eT, value, __result);
        }
    }
}
