using System;
using System.Reflection;
using NLog;
using Sandbox.Definitions;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class ThrustPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var Init = typeof(MyThrustDefinition).GetMethod
                ("Init", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            ctx.GetPattern(Init).Suffixes.Add(
                typeof(ThrustPatch).GetMethod(nameof(InitPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        //
        private static void InitPatched(MyThrustDefinition __instance)
        {
            try
            {
                __instance.ForceMagnitude =
                    __instance.ForceMagnitude * SentisOptimisationsPlugin.Config.ThrustPowerMultiplier;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn("InitPatched Exception ", e);
            }
        }
    }
}