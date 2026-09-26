using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sandbox.Game.World.Generator;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The price of a prefab worked out once per price calculator, not once per contract.
    ///
    /// <c>MyMinimalPriceCalculator.CalculatePrefabInformation</c> keeps what it works out for a prefab (its minimal price,
    /// repair price, PCU), yet never looks there first: every call reads the prefab's definition from disk again, goes
    /// through all its blocks and unloads it. The economy tick asks it for every contract of every station (the pirate
    /// PCU limit of a bounty, the price of a grid to haul or salvage): 0.4-0.6 s of one frame every economy tick on the
    /// stand (dotTrace).
    ///
    /// Here the names the calculator has already worked out, with the same production speed multiplier, are left out of
    /// the call; nothing left - no call. What it keeps is the game's own result: the definitions do not change during a
    /// session.
    /// </summary>
    [PatchShim]
    public static class PrefabPriceOnce
    {
        private static FieldInfo _prefabsInfo;

        // for each calculator: the multiplier each of its prefabs was worked out with
        private static readonly ConditionalWeakTable<MyMinimalPriceCalculator, Dictionary<string, float>> Done =
            new ConditionalWeakTable<MyMinimalPriceCalculator, Dictionary<string, float>>();

        [ThreadStatic] private static List<string> _asked;
        [ThreadStatic] private static float _askedMultiplier;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("PrefabPriceOnce", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var type = typeof(MyMinimalPriceCalculator);
            var method = type.GetMethod("CalculatePrefabInformation", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string[]), typeof(float) }, null)
                         ?? throw new MissingMethodException("MyMinimalPriceCalculator.CalculatePrefabInformation");
            _prefabsInfo = type.GetField("m_prefabsInfo", BindingFlags.Instance | BindingFlags.NonPublic)
                           ?? throw new MissingFieldException("MyMinimalPriceCalculator.m_prefabsInfo");
            ctx.GetPattern(method).Prefixes.Add(typeof(PrefabPriceOnce).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
            ctx.GetPattern(method).Suffixes.Add(typeof(PrefabPriceOnce).GetMethod(nameof(Suffix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>The names still to work out (the rest are known with this multiplier), or none: then no call.</summary>
        public static List<string> StillToDo(IEnumerable<string> names, float multiplier, IReadOnlyDictionary<string, float> done, Func<string, bool> known) =>
            names.Where(n => n != null && !(done.TryGetValue(n, out var m) && m == multiplier && known(n))).Distinct().ToList();

        private static bool Prefix(MyMinimalPriceCalculator __instance, ref string[] prefabNames, float baseCostProductionSpeedMultiplier)
        {
            _asked = null;
            if (prefabNames == null || prefabNames.Length == 0) return true;
            var done = Done.GetOrCreateValue(__instance);
            var info = (IDictionary)_prefabsInfo.GetValue(__instance);
            var todo = StillToDo(prefabNames, baseCostProductionSpeedMultiplier, done, info.Contains);
            if (todo.Count == 0) return false;
            if (todo.Count != prefabNames.Length) prefabNames = todo.ToArray();
            _asked = todo;
            _askedMultiplier = baseCostProductionSpeedMultiplier;
            return true;
        }

        private static void Suffix(MyMinimalPriceCalculator __instance)
        {
            if (_asked == null) return;
            var done = Done.GetOrCreateValue(__instance);
            var info = (IDictionary)_prefabsInfo.GetValue(__instance);
            foreach (var name in _asked)
                if (info.Contains(name)) done[name] = _askedMultiplier;
            _asked = null;
        }
    }
}
