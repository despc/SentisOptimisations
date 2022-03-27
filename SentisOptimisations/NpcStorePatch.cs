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
                    minimalPrice = 220000;
                }
                if (itemId.SubtypeName.Equals("Structure_t2"))
                {
                    minimalPrice = 2000000;
                }
                if (itemId.SubtypeName.Equals("Structure_t3"))
                {
                    minimalPrice = 5000000;
                }
                if (itemId.SubtypeName.Equals("Production_t2"))
                {
                    minimalPrice = 2000000;
                }
                if (itemId.SubtypeName.Equals("Production_t3"))
                {
                    minimalPrice = 5000000;
                }
                if (itemId.SubtypeName.Equals("Turret_t2"))
                {
                    minimalPrice = 2000000;
                }
                if (itemId.SubtypeName.Equals("Turret_t3"))
                {
                    minimalPrice = 5000000;
                }
                if (itemId.SubtypeName.Equals("PowerCell_t2"))
                {
                    minimalPrice = 300000;
                }
                if (itemId.SubtypeName.Equals("PowerCell_t3"))
                {
                    minimalPrice = 1000000;
                }
                if (itemId.SubtypeName.Equals("SolarCell_t2"))
                {
                    minimalPrice = 100000;
                }
                if (itemId.SubtypeName.Equals("SolarCell_t3"))
                {
                    minimalPrice = 200000;
                }
                if (itemId.SubtypeName.Equals("Thrust_t2"))
                {
                    minimalPrice = 500000;
                }
                if (itemId.SubtypeName.Equals("Thrust_t3"))
                {
                    minimalPrice = 2000000;
                }
                
                
                if (itemId.SubtypeName.Equals("Titanium"))
                {
                    if (itemId.TypeId.ToString().Contains("Ingot"))
                        minimalPrice = 500000;
                    else
                        minimalPrice = 50000;
                }
                if (itemId.SubtypeName.Equals("Copper"))
                {
                    if (itemId.TypeId.ToString().Contains("Ingot"))
                        minimalPrice = 400000;
                    else
                        minimalPrice = 40000;
                }
                if (itemId.SubtypeName.Equals("Zinc"))
                {
                    if (itemId.TypeId.ToString().Contains("Ingot"))
                        minimalPrice = 600000;
                    else
                        minimalPrice = 60000;
                }
            }
            catch (Exception e)
            {
                Log.Error("Exception in time PatchGetItemMinimalPrice", e);
            }
        }
    }
}