using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;

namespace FixTurrets.Clusters
{
    [PatchShim]
    public static class StockpileConcurrencyFix
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        static ConcurrentDictionary<MyConstructionStockpile, List<MyStockpileItem>> stockpiles = new ConcurrentDictionary<MyConstructionStockpile, List<MyStockpileItem>>();
        public static void Patch(PatchContext ctx)
        {
            var MethodClearSyncList = typeof(MyConstructionStockpile).GetMethod
                ("ClearSyncList", BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodClearSyncList).Prefixes.Add(
                typeof(StockpileConcurrencyFix).GetMethod(nameof(MethodClearSyncListPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodGetSyncList = typeof(MyConstructionStockpile).GetMethod
                ("GetSyncList", BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodGetSyncList).Prefixes.Add(
                typeof(StockpileConcurrencyFix).GetMethod(nameof(MethodGetSyncListPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodAddSyncItem = typeof(MyConstructionStockpile).GetMethod
                ("AddSyncItem", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodAddSyncItem).Prefixes.Add(
                typeof(StockpileConcurrencyFix).GetMethod(nameof(AddSyncItemPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool MethodClearSyncListPatched(MyConstructionStockpile __instance)
        {
            if (!SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.ClustersEnabled)
            {
                return true;
            }

            try
            {
                List<MyStockpileItem> m_syncItems;
                if (stockpiles.TryGetValue(__instance, out m_syncItems))
                {
                    m_syncItems.Clear();
                    return false;
                }
                stockpiles[__instance] = new List<MyStockpileItem>();
                return false;
            }
            catch (Exception e)
            {
                Log.Error("MethodClearSyncListPatched", e);
                return true;
            }
        }
        
        private static bool MethodGetSyncListPatched(MyConstructionStockpile __instance, ref List<MyStockpileItem> __result)
        {
            if (!SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.ClustersEnabled)
            {
                return true;
            }

            try
            {
                List<MyStockpileItem> m_syncItems;
                if (stockpiles.TryGetValue(__instance, out m_syncItems))
                {
                    __result = m_syncItems;
                    return false;
                }

                m_syncItems = new List<MyStockpileItem>();
                stockpiles[__instance] = m_syncItems;
                __result = m_syncItems;
                return false;
            }
            catch (Exception e)
            {
                Log.Error("MethodGetSyncListPatched", e);
                return true;
            }
        }
        
        private static bool AddSyncItemPatched(MyStockpileItem diffItem, MyConstructionStockpile __instance)
        {
            if (!SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.ClustersEnabled)
            {
                return true;
            }
            
            int index = 0;
            List<MyStockpileItem> m_syncItems;
            if (!stockpiles.TryGetValue(__instance, out m_syncItems))
            {
                m_syncItems = new List<MyStockpileItem>();
                stockpiles[__instance] = m_syncItems;
            }
            foreach (MyStockpileItem syncItem in m_syncItems)
            {
                if (syncItem.Content.CanStack(diffItem.Content))
                {
                    m_syncItems[index] = new MyStockpileItem()
                    {
                        Amount = syncItem.Amount + diffItem.Amount,
                        Content = syncItem.Content
                    };
                    return false;
                }
                ++index;
            }
            m_syncItems.Add(diffItem);
            return false;
        }
    }
}