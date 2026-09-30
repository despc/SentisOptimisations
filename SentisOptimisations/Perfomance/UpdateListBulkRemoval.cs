using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Torch.Managers.PatchManager;
using VRage.Collections;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The entities taken off the 10- and 100-frame update lists in one pass instead of one search each.
    ///
    /// <c>MyParallelEntityUpdateOrchestrator</c> keeps those entities in <c>TypeSortedCachingList&lt;MyEntity&gt;</c>s
    /// (inside <c>MyDistributedTypeUpdater</c>): one list, grouped by type, plus the index of the last entity of each
    /// group. A removal waits in <c>m_toRemove</c> until <c>ApplyRemovals</c>, which then takes each one off with
    /// <c>List.Remove</c> - a search of the whole list - and moves the group ends after it. Deleting many grids at once
    /// is quadratic: on the stand, after the 500 ships of load_test_500 went (1.4 million blocks), the game thread stood
    /// in <c>ApplyRemovals</c> long enough for Torch's watchdog (60 s) to kill the server (30.09.2026).
    ///
    /// With more than a few removals waiting the list is rebuilt in one pass: each list entry equal to a waiting one is
    /// dropped (the first occurrence first, as <c>List.Remove</c> does), and each group end moves back by the number of
    /// entries dropped from its group and the groups before it - what the one-by-one removals add up to.
    /// </summary>
    [PatchShim]
    public static class UpdateListBulkRemoval
    {
        /// <summary>Waiting removals from which the one pass is used; below it the game's own loop is as quick.</summary>
        public const int MinBatch = 16;

        private static readonly Func<TypeSortedCachingList<MyEntity>, List<MyEntity>> List =
            Accessors.Field<TypeSortedCachingList<MyEntity>, List<MyEntity>>("m_list");
        private static readonly Func<TypeSortedCachingList<MyEntity>, List<MyEntity>> ToRemove =
            Accessors.Field<TypeSortedCachingList<MyEntity>, List<MyEntity>>("m_toRemove");
        private static readonly Func<TypeSortedCachingList<MyEntity>, Dictionary<Type, int>> TypeIndexes =
            Accessors.Field<TypeSortedCachingList<MyEntity>, Dictionary<Type, int>>("m_typeIndexes");
        private static readonly Func<TypeSortedCachingList<MyEntity>, List<int>> SortIndexes =
            Accessors.Field<TypeSortedCachingList<MyEntity>, List<int>>("m_sortIndexes");

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("UpdateListBulkRemoval", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            // Harmony, not Torch: the target is a method of a generic class.
            var target = typeof(TypeSortedCachingList<MyEntity>).GetMethod(nameof(TypeSortedCachingList<MyEntity>.ApplyRemovals),
                             BindingFlags.Instance | BindingFlags.Public)
                         ?? throw new MissingMethodException("TypeSortedCachingList.ApplyRemovals");
            global::SentisOptimisationsPlugin.CrashFix.CrashFixPatch.harmony.Patch(target, prefix: new HarmonyMethod(typeof(UpdateListBulkRemoval).GetMethod(nameof(ApplyRemovalsPrefix),
                BindingFlags.Static | BindingFlags.NonPublic)));
        }

        // Reference-type instantiations share this code: any other T goes the game's way.
        private static bool ApplyRemovalsPrefix(object __instance)
        {
            if (!(__instance is TypeSortedCachingList<MyEntity> updateList)) return true;
            var toRemove = ToRemove(updateList);
            if (toRemove.Count < MinBatch) return true;
            RemoveAll(List(updateList), toRemove, TypeIndexes(updateList), SortIndexes(updateList));
            toRemove.Clear();
            return false;
        }

        /// <summary>
        /// Removes <paramref name="toRemove"/> from <paramref name="list"/> as that many <c>List.Remove</c> calls would,
        /// and moves the group ends of <paramref name="sortIndexes"/> the way the game's removal does for each.
        /// </summary>
        public static void RemoveAll<T>(List<T> list, List<T> toRemove, Dictionary<Type, int> typeIndexes, List<int> sortIndexes)
        {
            var pending = new Dictionary<T, int>(toRemove.Count, EqualityComparer<T>.Default);
            foreach (var item in toRemove)
            {
                // The game skips an entity of a type the list never had (it is not in the list either).
                if (item == null || !typeIndexes.ContainsKey(item.GetType())) continue;
                pending.TryGetValue(item, out var count);
                pending[item] = count + 1;
            }
            if (pending.Count == 0) return;

            var removedPerGroup = new int[sortIndexes.Count];
            var kept = 0;
            for (var i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                if (entry != null && pending.TryGetValue(entry, out var count) && count > 0)
                {
                    if (count == 1) pending.Remove(entry);
                    else pending[entry] = count - 1;
                    // List.Remove finds an entry equal to the item; the item's own type names the group it moves.
                    removedPerGroup[typeIndexes[entry.GetType()]]++;
                    continue;
                }
                list[kept++] = entry;
            }
            list.RemoveRange(kept, list.Count - kept);

            var shift = 0;
            for (var group = 0; group < sortIndexes.Count; group++)
            {
                shift += removedPerGroup[group];
                if (shift != 0) sortIndexes[group] -= shift;
            }
        }
    }
}
