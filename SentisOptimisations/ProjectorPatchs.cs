using System.Reflection;
using NLog;
using Sandbox.Game.Entities.Blocks;
using Torch.Managers.PatchManager;

namespace FixTurrets
{
    [PatchShim]
    public static class FixProjectorPcu
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            Log.Info("FixProjectorPcu Patch init");

            var initializeClipboardMethodInfo = typeof(MyProjectorBase).GetMethod("InitializeClipboard",
                BindingFlags.Instance | BindingFlags.NonPublic);
            ctx.GetPattern(initializeClipboardMethodInfo).Prefixes.Add(
                typeof(FixProjectorPcu).GetMethod(nameof(DisableInitializeClipboard),
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool DisableInitializeClipboard()
        {
            return false;
        }
    }
}