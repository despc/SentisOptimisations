using System;
using System.Reflection;
using NLog;
using Torch.Commands;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class TorchPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("TorchPatch", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var PluginsM = typeof(TorchCommands).GetMethod
                ("Plugins", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

            ctx.GetPattern(PluginsM).Prefixes.Add(
                typeof(TorchPatch).GetMethod(nameof(PluginsMPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool PluginsMPatched()
        {
        try
        {
            return false;
        


            }
                catch (Exception __guard_e)
                {
                    Log.Error("PluginsMPatched exception " + __guard_e);
                    return true;  // fall back to vanilla behavior
                }
        }
    }
}