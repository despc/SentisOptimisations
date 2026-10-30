using System;
using NLog;
using Torch.Managers.PatchManager;

namespace SentisOptimisations
{
    /// <summary>
    /// Torch applies [PatchShim] registrations on the main init path: any exception thrown
    /// from a Patch(PatchContext) method takes the whole server down. This guard isolates
    /// a failing patch class so a broken target only disables that feature.
    /// </summary>
    public static class PatchGuard
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Run(string shimName, PatchContext ctx, Action<PatchContext> register)
        {
            // an abandoned Harmony mutex let go first: else this class's first Harmony patch throws and is skipped
            var healed = HarmonyMutex.Heal();
            if (healed != null) Log.Warn($"Before '{shimName}': {healed}");
            try
            {
                register(ctx);
            }
            catch (Exception e)
            {
                if (HarmonyMutex.IsAbandoned(e))
                {
                    // this thread holds it now, and Harmony never lets it go: let go, or every other server's patching waits
                    Log.Error(e, $"Patch registration failed for '{shimName}': Harmony's mutex abandoned by another server; " +
                                 $"{HarmonyMutex.Heal() ?? "the mutex fine"}. Patches from this class are skipped: restart this server.");
                    return;
                }
                Log.Error(e, $"Patch registration failed for '{shimName}'; patches from this class are skipped.");
            }
        }
    }
}
