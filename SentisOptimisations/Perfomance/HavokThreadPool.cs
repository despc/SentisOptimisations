using System;
using System.Reflection;
using Torch.Managers.PatchManager;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// How many worker threads Havok steps the physics on. MyPhysics.LoadData makes the pool once per
    /// world load from IVRageSystem.OptimalHavokThreadCount, which is null on Windows: Havok then picks
    /// the count itself (7 on a 16-thread CPU). The getter is patched to return the config value
    /// (default 80% of the logical processors), so a change takes effect with the next world load.
    /// The pool is not swapped at run time: extra pools made while the world ran crashed Havok.
    /// </summary>
    [PatchShim]
    public static class HavokThreadPool
    {
        /// <summary>80% of the logical processors, at least one.</summary>
        public static int DefaultThreads => Math.Max(1, (int)Math.Round(Environment.ProcessorCount * 0.8));

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("HavokThreadPool", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var system = Type.GetType("VRage.Platform.Windows.Sys.MyWindowsSystem, VRage.Platform.Windows", true);
            var getter = system.GetProperty("OptimalHavokThreadCount", BindingFlags.Instance | BindingFlags.Public)?.GetGetMethod()
                         ?? throw new MissingMethodException(system.FullName, "get_OptimalHavokThreadCount");
            ctx.GetPattern(getter).Suffixes.Add(typeof(HavokThreadPool).GetMethod(nameof(OptimalThreadsSuffix),
                BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static void OptimalThreadsSuffix(ref int? __result)
        {
            var threads = SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config?.PhysicsThreads ?? 0;
            if (threads <= 0) return;
            threads = Math.Min(threads, Environment.ProcessorCount);
            if (__result != threads)
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Info($"Havok threads: {threads} (Physics threads)");
            __result = threads;
        }
    }
}
