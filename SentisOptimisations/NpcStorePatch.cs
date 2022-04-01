using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Documents;
using NLog;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World.Generator;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.ObjectBuilders.Definitions;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class NpcStorePatch
    {

            
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        
        public static string[] prohibitedStoreItems = new string[] {
            "Structure_t2",
            "Structure_t3",
            "Production_t2",
            "Production_t3",
            "Turret_t2",
            "Turret_t3",
            "PowerCell_t2",
            "PowerCell_t3",
            "SolarCell_t2",
            "SolarCell_t3",
            "Thrust_t2",
            "Thrust_t3",
            "Titanium",
            "Copper",
            "Zinc",
            "TradePack_t1",
            "TradePack_t2",
            "TradePack_t3",
            "Tech_Structure",
            "Tech_Turret",
            "Tech_Production",
            "Tech_Solar",
            "Tech_Power",
            "Tech_Ammo"};
        
        public static void Patch(PatchContext ctx)
        {
            var MethodGetItemMinimalPrice = typeof(MyMinimalPriceCalculator).GetMethod(
                nameof(MyMinimalPriceCalculator.TryGetItemMinimalPrice), BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodGetItemMinimalPrice).Prefixes.Add(
                typeof(NpcStorePatch).GetMethod(nameof(PatchGetItemMinimalPrice),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            
            // Sandbox.Game.Multiplayer.
                
            var MethodInit = typeof(MyFactionCollection).GetMethod(
                nameof(MyFactionCollection.Init), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            //
            ctx.GetPattern(MethodInit).Prefixes.Add(
                typeof(NpcStorePatch).GetMethod(nameof(MethodInitPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }


        private static void MethodInitPatch(MyFactionCollection __instance, MyObjectBuilder_FactionCollection builder)
        {
            try
            {
                foreach (var myObjectBuilderFaction in builder.Factions)
                {
                    foreach (var myObjectBuilderStation in myObjectBuilderFaction.Stations)
                    {
                        foreach (var myObjectBuilderStoreItem in new List<MyObjectBuilder_StoreItem>(myObjectBuilderStation.StoreItems))
                        {
                            if (isProhibited(myObjectBuilderStoreItem))
                            {
                                myObjectBuilderStation.StoreItems.Remove(myObjectBuilderStoreItem);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }

        }

        private static bool isProhibited(MyObjectBuilder_StoreItem myObjectBuilderStoreItem)
        {
            if (myObjectBuilderStoreItem.ItemType == ItemTypes.PhysicalItem)
            {
                if (prohibitedStoreItems.Contains(myObjectBuilderStoreItem.Item.Value.SubtypeId))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool PatchGetItemMinimalPrice(MyDefinitionId itemId, ref int minimalPrice, ref bool __result)
        {
            try
            {
                if (itemId.SubtypeName.Equals("ZoneChip"))
                {
                    minimalPrice = 220000;
                    __result = true;
                    return false;
                }
                if (prohibitedStoreItems.Contains(itemId.SubtypeName))
                {
                    __result = false;
                    minimalPrice = -1;
                    return false;
                }
            }
            catch (Exception e)
            {
                Log.Error("Exception in time PatchGetItemMinimalPrice", e);
            }

            return true;
        }
    }
}