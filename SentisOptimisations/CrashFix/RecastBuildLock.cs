using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// One navmesh tile built at a time across the whole server (the prod's crash of 25.11.2026: <c>STATUS_HEAP_CORRUPTION</c>
    /// in <c>RecastDetour.dll</c> under <c>MyNavmeshManager.GenerateTile</c> on a worker).
    ///
    /// <c>RecastDetour_CreateNavmeshTile</c> puts the build's <c>rcConfig</c> - cell size and height, the agent's height,
    /// radius and climb, the border (radius + 3), the width and height of the tile's grid (tile + 2 borders), the region
    /// areas, the vertices a polygon - into one static of the dll, and its steps read it back from there all through the
    /// build: the heightfield, the compact field, the regions and contours are allocated by the width it held then and
    /// filled by the width it holds now. Each animal navmesh (<c>MyNavmeshManager</c>, a planet's sector) builds one tile
    /// at a time, on a ParallelTasks worker, and several build at once; their options come from the agent of the sector
    /// (a wolf's radius and height are not a spider's). Two at once with different agents and one writes the other's
    /// arrays past their end - the dump: a heap block overwritten with 16-bit pairs, found by <c>free</c> in the build.
    ///
    /// Here the build holds one lock (a Harmony prefix takes it, the finalizer lets it go - also when the call throws:
    /// a Torch suffix would not run then and every navmesh would wait for ever). The builds run on workers only, the
    /// game thread never waits for this lock (its path queries do not take it).
    /// </summary>
    [PatchShim]
    public static class RecastBuildLock
    {
        private static readonly Harmony Harmony = new Harmony("SentisOptimisations.RecastBuildLock");
        private static readonly object Gate = new object();

        /// <summary>Tiles built; of them, the ones that found another build going and waited; the longest wait, ms.</summary>
        internal static long Builds, Waited;
        internal static double LongestWaitMs;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("RecastBuildLock", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var wrapper = Type.GetType("RecastDetour.MyRDWrapper, RecastDetourWrapper")
                          ?? typeof(Sandbox.Game.AI.Pathfinding.RecastDetour.MyNavmeshManager).Assembly.GetReferencedAssemblies()
                              .Where(a => a.Name == "RecastDetourWrapper").Select(a => Assembly.Load(a).GetType("RecastDetour.MyRDWrapper")).FirstOrDefault()
                          ?? throw new TypeLoadException("RecastDetour.MyRDWrapper");
            var build = wrapper.GetMethod("CreateNavmeshTile", BindingFlags.Instance | BindingFlags.Public)
                        ?? throw new MissingMethodException("MyRDWrapper", "CreateNavmeshTile");
            Harmony.Patch(build,
                prefix: new HarmonyMethod(typeof(RecastBuildLock).GetMethod(nameof(Enter), BindingFlags.Static | BindingFlags.NonPublic)),
                finalizer: new HarmonyMethod(typeof(RecastBuildLock).GetMethod(nameof(Leave), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static void Enter(out bool __state)
        {
            __state = false;
            if (!Monitor.TryEnter(Gate))
            {
                var watch = Stopwatch.StartNew();
                Monitor.Enter(Gate);
                Interlocked.Increment(ref Waited);
                var ms = watch.Elapsed.TotalMilliseconds;
                if (ms > LongestWaitMs) LongestWaitMs = ms;
            }
            __state = true;
            Interlocked.Increment(ref Builds);
        }

        private static Exception Leave(Exception __exception, bool __state)
        {
            if (__state) Monitor.Exit(Gate);
            return __exception;
        }
    }
}
