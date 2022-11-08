using System.Reflection;
using NLog;
using Torch.Commands;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public class TorchPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var PluginsM = typeof(TorchCommands).GetMethod
                ("Plugins", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

            ctx.GetPattern(PluginsM).Prefixes.Add(
                typeof(TorchPatch).GetMethod(nameof(PluginsMPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool PluginsMPatched()
        {
            return false;
        }
    }
}