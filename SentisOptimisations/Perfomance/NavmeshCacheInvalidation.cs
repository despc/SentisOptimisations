using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The navmesh's voxel mesh cache cleared of changed cells in one pass instead of a quadratic one.
    ///
    /// Every voxel change near an animal's navmesh (a drill, a grinder through the ground) queues the changed box in
    /// <c>MyNavigationInputMesh</c>; the next navmesh tile built on a worker thread (<c>AddVoxelMesh</c>) first calls
    /// <c>CheckCacheValidity</c>, which for each queued box walks the cache with <c>m_meshCache.ElementAt(i)</c> - and
    /// <c>ElementAt</c> on a dictionary walks it from the start every time: cells² steps a box. The cache only grows
    /// while the server runs. On the production server (dotTrace Timeline, 04.10.2026) it held a ParallelTasks worker
    /// 97.8% of the time, 2.84 s of 2.9 s, in this method alone.
    ///
    /// Here each box takes one walk of the cache, and the result is the game's own to the letter: the first cell in the
    /// dictionary's order that lies in the box is removed, and only that one (the game breaks there - one cell a box;
    /// the other cells of a bigger box stay cached, as they always have).
    /// </summary>
    [PatchShim]
    public static class NavmeshCacheInvalidation
    {
        private static FieldInfo _queue, _cache, _min, _max;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("NavmeshCacheInvalidation", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(Sandbox.Game.AI.Pathfinding.RecastDetour.MyNavmeshManager).Assembly
                           .GetType("Sandbox.Game.AI.Pathfinding.RecastDetour.MyNavigationInputMesh")
                       ?? throw new TypeLoadException("MyNavigationInputMesh");
            var method = type.GetMethod("CheckCacheValidity", any, null, Type.EmptyTypes, null)
                         ?? throw new MissingMethodException("MyNavigationInputMesh.CheckCacheValidity");
            _queue = type.GetField("m_invalidateMeshCacheCoord", any) ?? throw new MissingFieldException("m_invalidateMeshCacheCoord");
            _cache = type.GetField("m_meshCache", any) ?? throw new MissingFieldException("m_meshCache");
            var interval = type.GetNestedType("CacheInterval", any) ?? throw new TypeLoadException("CacheInterval");
            _min = interval.GetField("Min", any) ?? throw new MissingFieldException("CacheInterval.Min");
            _max = interval.GetField("Max", any) ?? throw new MissingFieldException("CacheInterval.Max");
            if (!typeof(IDictionary).IsAssignableFrom(_cache.FieldType) || !typeof(IList).IsAssignableFrom(_queue.FieldType))
                throw new InvalidOperationException("NavmeshCacheInvalidation: unexpected field types");
            ctx.GetPattern(method).Prefixes.Add(typeof(NavmeshCacheInvalidation).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>Whether a cell lies in the box from min to max, both ends in.</summary>
        public static bool InBox(Vector3I key, Vector3I min, Vector3I max) =>
            key.X >= min.X && key.Y >= min.Y && key.Z >= min.Z && key.X <= max.X && key.Y <= max.Y && key.Z <= max.Z;

        /// <summary>
        /// The game's CheckCacheValidity on the keys: for each box in turn, the first key in the dictionary's order
        /// that lies in it is removed. One walk a box.
        /// </summary>
        public static void RemoveFirstInEachBox(IDictionary cache, List<(Vector3I Min, Vector3I Max)> boxes)
        {
            foreach (var box in boxes)
            {
                object found = null;
                foreach (var key in cache.Keys)
                {
                    if (!InBox((Vector3I)key, box.Min, box.Max)) continue;
                    found = key;
                    break;
                }
                if (found != null) cache.Remove(found);
            }
        }

        private static bool Prefix(object __instance)
        {
            var queue = (IList)_queue.GetValue(__instance);
            if (queue.Count <= 0) return false;
            var cache = (IDictionary)_cache.GetValue(__instance);
            // (the boxes taken off the queue first and the queue cleared, as the game does)
            var boxes = new List<(Vector3I Min, Vector3I Max)>(queue.Count);
            foreach (var item in queue) boxes.Add(((Vector3I)_min.GetValue(item), (Vector3I)_max.GetValue(item)));
            queue.Clear();
            RemoveFirstInEachBox(cache, boxes);
            return false;
        }
    }
}
