using System;
using System.Reflection;
using NLog;
using Sandbox.Game.World.Generator;
using Torch.Managers.PatchManager;
using VRage.Game;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class NpcStorePatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var MethodGetItemMinimalPrice = typeof(MyMinimalPriceCalculator).GetMethod(
                nameof(MyMinimalPriceCalculator.TryGetItemMinimalPrice), BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodGetItemMinimalPrice).Suffixes.Add(
                typeof(NpcStorePatch).GetMethod(nameof(PatchGetItemMinimalPrice),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static void PatchGetItemMinimalPrice(MyDefinitionId itemId, ref int minimalPrice)
        {
            try
            {
                if (itemId.SubtypeName.Equals("ZoneChip"))
                {
                    minimalPrice = 90000;
                }
            }
            catch (Exception e)
            {
                Log.Error("Exception in time PatchGetItemMinimalPrice", e);
            }
        }
    }
}