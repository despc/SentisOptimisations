using System;
using System.Reflection;
using NLog;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Blocks;
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
            
            var GeneratorInit = typeof(MyOxygenGeneratorDefinition).GetMethod
                ("Init", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            ctx.GetPattern(GeneratorInit).Suffixes.Add(
                typeof(GasTankPatch).GetMethod(nameof(GeneratorInitPatched),
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
        
        private static void GeneratorInitPatched(MyOxygenGeneratorDefinition __instance)
        {
            try
            {
                __instance.IceConsumptionPerSecond = __instance.IceConsumptionPerSecond * SentisOptimisationsPlugin.Config.H2GenMultiplier;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn("MyGasTankInitPatched Exception ", e);
            }
        }
    }
}