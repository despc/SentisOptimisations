using System;
using System.Reflection;
using NLog;
using Sandbox.Definitions;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public class GasTankPatch
    {
        
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var MyGasTankInit = typeof(MyGasTankDefinition).GetMethod
                ("Init", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            ctx.GetPattern(MyGasTankInit).Suffixes.Add(
                typeof(GasTankPatch).GetMethod(nameof(MyGasTankInitPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

        }

        //
        private static void MyGasTankInitPatched(MyGasTankDefinition __instance)
        {
            try
            {
                __instance.Capacity = __instance.Capacity * SentisOptimisationsPlugin.Config.GasTankCapacityMultiplier;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn("MyGasTankInitPatched Exception ", e);
            }
        }
    }
}