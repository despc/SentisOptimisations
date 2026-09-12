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
            try
            {
                register(ctx);
            }
            catch (Exception e)
            {
                Log.Error(e, $"Patch registration failed for '{shimName}'; patches from this class are skipped.");
            }
        }
    }
}
