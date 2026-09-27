using System;
using System.Reflection;
using System.Threading;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The managed memory the game asks for read on a thread of its own once a second, not on the game thread.
    ///
    /// <c>MyWindowsSystem.GetGCMemory</c> (for the session's "GC Memory" line every 30 seconds, and Torch's view) is
    /// <c>GC.GetTotalMemory(false)</c>, which takes the collector's lock: with the server GC and a background
    /// collection under way that is a wait of 10-12 ms on the game thread (Watcher, "gc_memory"). Here the game gets
    /// the value read at most a second ago by a timer.
    /// </summary>
    [PatchShim]
    public static class GcMemoryCached
    {
        /// <summary>How often the value is read.</summary>
        public static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

        private static float _usedMb = -1;
        private static Timer _timer;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("GcMemoryCached", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var type = Type.GetType("VRage.Platform.Windows.Sys.MyWindowsSystem, VRage.Platform.Windows", false)
                       ?? throw new TypeLoadException("VRage.Platform.Windows.Sys.MyWindowsSystem");
            var method = type.GetMethod("GetGCMemory", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         ?? throw new MissingMethodException("MyWindowsSystem.GetGCMemory");
            ctx.GetPattern(method).Prefixes.Add(typeof(GcMemoryCached).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
            _timer = new Timer(_ => Read(), null, TimeSpan.Zero, Every);
        }

        private static void Read() => _usedMb = GC.GetTotalMemory(false) / 1024f / 1024f;

        private static bool Prefix(out float allocated, out float used)
        {
            var value = _usedMb;
            if (value < 0) Read();
            allocated = used = _usedMb;
            return false;
        }
    }
}
