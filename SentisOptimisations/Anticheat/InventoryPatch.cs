using System;
using System.Collections.Generic;
using System.Reflection;
using NAPI;
using NLog;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities.Inventory;
using Sandbox.ModAPI;
using SentisOptimisations.DelayedLogic;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI.Ingame;
using VRage.Sync;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class InventoryPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var TransferItemsFromMethod = typeof(MyInventory).GetMethod
                ("TransferItemsFrom", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            ctx.GetPattern(TransferItemsFromMethod).Prefixes.Add(
                typeof(InventoryPatch).GetMethod(nameof(TransferItemsFromPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var RefreshVolumeAndMassMethod = typeof(MyInventory).GetMethod
                ("RefreshVolumeAndMass", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            // Sandbox.Game.MyInventory.RefreshVolumeAndMass -- ебануть асинк
            ctx.GetPattern(RefreshVolumeAndMassMethod).Prefixes.Add(
                typeof(InventoryPatch).GetMethod(nameof(RefreshVolumeAndMassPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }
        
        private static bool RefreshVolumeAndMassPatched(MyInventory __instance)
        {
            if (!SentisOptimisationsPlugin.Config.EnableAsyncRecalculateInventory)
            {
                return true;
            }
            DelayedProcessor.Instance.AddDelayedAction(DateTime.Now, () =>
            {
                try
                {
                    MyFixedPoint myFixedPoint1 = __instance.CurrentVolume;
                    VRage.Sync.Sync<MyFixedPoint, SyncDirection.FromServer> m_currentMass =
                        (Sync<MyFixedPoint, SyncDirection.FromServer>)__instance.easyGetField("m_currentMass");
                    VRage.Sync.Sync<MyFixedPoint, SyncDirection.FromServer> m_currentVolume =
                        (Sync<MyFixedPoint, SyncDirection.FromServer>)__instance.easyGetField("m_currentVolume");
                    m_currentMass.Value = ((MyFixedPoint) 0);
                    m_currentVolume.Value = (MyFixedPoint) 0;
                    MyFixedPoint myFixedPoint2 = (MyFixedPoint) 0;
                    MyFixedPoint myFixedPoint3 = (MyFixedPoint) 0;
                    List<MyPhysicalInventoryItem> myPhysicalInventoryItems = (List<MyPhysicalInventoryItem>)__instance.easyGetField("m_items");
                    foreach (MyPhysicalInventoryItem inventoryItem in myPhysicalInventoryItems)
                    {
                        MyInventoryItemAdapter inventoryItemAdapter = MyInventoryItemAdapter.Static;
                        inventoryItemAdapter.Adapt((VRage.Game.ModAPI.Ingame.IMyInventoryItem) inventoryItem);
                        myFixedPoint2 += inventoryItemAdapter.Mass * inventoryItem.Amount;
                        myFixedPoint3 += inventoryItemAdapter.Volume * inventoryItem.Amount;
                    }
                    MyAPIGateway.Utilities.InvokeOnGameThread((Action) (() =>
                    {
                        try
                        {
                            m_currentMass.Value = myFixedPoint2;
                            m_currentVolume.Value = myFixedPoint3;
                            if (!(myFixedPoint1 != (MyFixedPoint) m_currentVolume))
                                return;
            
                            Raise(__instance, "OnVolumeChanged", new object[]
                            {
                                (VRage.Game.ModAPI.IMyInventory)__instance,
                                (float)myFixedPoint1, (float)m_currentVolume.Value
                            });
                        }
                        catch (Exception ex)
                        {
                            if (SentisOptimisationsPlugin.Config.EnableMainDebugLogs)
                            {
                                Log.Error(ex, "Recelculate mass excption");
                            }
                        }
                    }));
                }
                catch (Exception e)
                {
                    if (SentisOptimisationsPlugin.Config.EnableMainDebugLogs)
                    {
                        SentisOptimisationsPlugin.Log.Error(e, "Recelculate mass excption");
                    }
                }
            });
            
            // __instance.OnVolumeChanged.InvokeIfNotNull<VRage.Game.ModAPI.IMyInventory, float, float>((VRage.Game.ModAPI.IMyInventory) this, 
            //     (float) myFixedPoint1, (float) this.m_currentVolume.Value);

            return false;
        }
        
        internal static void Raise(this object source, string eventName, object[] eventArgs)
        {
            var eventDelegate = (MulticastDelegate)source.GetType().GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(source);
            if (eventDelegate != null)
            {
                foreach (var handler in eventDelegate.GetInvocationList())
                {
                    handler.Method.Invoke(handler.Target, new object[] { source, eventArgs });
                }
            }
        }
        private static bool TransferItemsFromPatched(IMyInventory sourceInventory,
            int sourceItemIndex, ref MyFixedPoint? amount, ref bool __result)
        {
            if (amount.HasValue && amount.Value <= 0)
            {
                __result = true;
                return false;
            }

            if (sourceInventory is MyInventory src && src.IsItemAt(sourceItemIndex))
            {
                MyPhysicalInventoryItem physicalInventoryItem = src.GetItems()[sourceItemIndex];
                var ob = physicalInventoryItem.Content;
                var isAtomicItem = ob is MyObjectBuilder_PhysicalGunObject ||
                                   ob is MyObjectBuilder_Component ||
                                   ob is MyObjectBuilder_GasContainerObject ||
                                   ob is MyObjectBuilder_AmmoMagazine;
                if (amount.HasValue && isAtomicItem && !MyFixedPoint.IsIntegral(amount.Value))
                {
                    amount = MyFixedPoint.Floor(amount.Value);
                    return true;
                }
            }
            return true;
        }
    }
}