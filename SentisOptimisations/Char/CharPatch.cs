using System;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities.Character.Components;
using SentisOptimisations;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class CharPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            
            var MethodInit = typeof(MyCharacterOxygenComponent).GetMethod("Init",
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly | BindingFlags.Public);

            ctx.GetPattern(MethodInit).Prefixes.Add(
                typeof(CharPatch).GetMethod(nameof(MethodInitPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool MethodInitPatched()
        {
            try
            {
                ReflectionUtils.SetPrivateStaticField(typeof(MyCharacterOxygenComponent), "GAS_REFILL_RATION", 0.8f);
            }
            catch (Exception e)
            {
                Log.Error("Exception in CleanupPatch", e);
            }
            return true;
        }
    }
}