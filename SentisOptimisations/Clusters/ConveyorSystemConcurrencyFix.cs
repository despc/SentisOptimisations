using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.Utils;

namespace FixTurrets.Clusters
{
    [PatchShim]
    public class ConveyorSystemConcurrencyFix
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var MethodPullItems = typeof(MyGridConveyorSystem).GetMethod
                (nameof(MyGridConveyorSystem.PullItems), BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodPullItems).Prefixes.Add(
                typeof(ConveyorSystemConcurrencyFix).GetMethod(nameof(PullItemsPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        // private static MyGridConveyorSystem.PullRequestItemSet m_tmpRequestedItemSet
        // {
        //     get
        //     {
        //         if (MyGridConveyorSystem.m_tmpRequestedItemSetPerThread == null)
        //             MyGridConveyorSystem.m_tmpRequestedItemSetPerThread = new MyGridConveyorSystem.PullRequestItemSet();
        //         return MyGridConveyorSystem.m_tmpRequestedItemSetPerThread;
        //     }
        // }
        private static bool PullItemsPatched(MyGridConveyorSystem __instance, MyInventoryConstraint inventoryConstraint,
            MyFixedPoint amount,
            IMyConveyorEndpointBlock start,
            MyInventory destinationInventory, ref MyFixedPoint __result)
        {
            try
            {
                MyFixedPoint myFixedPoint1 = (MyFixedPoint) 0;
                if ((double) destinationInventory.VolumeFillFactor >= 0.990000009536743 ||
                    inventoryConstraint == null || amount == (MyFixedPoint) 0)
                {
                    __result = myFixedPoint1;
                    return false;
                }
                    
                PullRequestItemSet m_tmpRequestedItemSet = new PullRequestItemSet();
                m_tmpRequestedItemSet.Set(inventoryConstraint);
                var conveyorEndpointMapping = ReflectionUtils.InvokeInstanceMethod(typeof(MyGridConveyorSystem), __instance,
                    "GetConveyorEndpointMapping", new object[] {start});
                // MyGridConveyorSystem.ConveyorEndpointMapping conveyorEndpointMapping = __instance.GetConveyorEndpointMapping(start);
                if (PullElements(conveyorEndpointMapping) != null)
                {
                    for (int index1 = 0; index1 < PullElements(conveyorEndpointMapping).Count; ++index1)
                    {
                        if (PullElements(conveyorEndpointMapping)[index1] is MyCubeBlock pullElement)
                        {
                            int inventoryCount = pullElement.InventoryCount;
                            for (int index2 = 0; index2 < inventoryCount; ++index2)
                            {
                                MyInventory inventory = MyEntityExtensions.GetInventory(pullElement, index2);
                                if ((inventory.GetFlags() & MyInventoryFlags.CanSend) != (MyInventoryFlags) 0 &&
                                    inventory != destinationInventory)
                                {
                                    List<MyPhysicalInventoryItem> m_tmpInventoryItems = new List<MyPhysicalInventoryItem>();
                                        foreach (MyPhysicalInventoryItem physicalInventoryItem in inventory.GetItems())
                                        {
                                            MyDefinitionId id = physicalInventoryItem.Content.GetId();
                                            // var canTransfer = MyGridConveyorSystem.CanTransfer(start,
                                            //     conveyorEndpointMapping.pullElements[index1], id, false);
                                            bool canTransfer = (bool) ReflectionUtils.InvokeStaticMethod(typeof(MyGridConveyorSystem), 
                                                "CanTransfer", new object[]{start, PullElements(conveyorEndpointMapping)[index1], id, false, false});
                                            if (m_tmpRequestedItemSet.Contains(id) &&
                                                (!(physicalInventoryItem.Content is MyObjectBuilder_GasContainerObject
                                                    content) || (double) content.GasLevel < 1.0) &&
                                                canTransfer)
                                                m_tmpInventoryItems.Add(physicalInventoryItem);
                                        }

                                        foreach (MyPhysicalInventoryItem tmpInventoryItem in m_tmpInventoryItems)
                                        {
                                            MyFixedPoint myFixedPoint2 =
                                                MyFixedPoint.Min(tmpInventoryItem.Amount, amount);
                                            MyFixedPoint myFixedPoint3 = MyInventory.Transfer(inventory,
                                                destinationInventory, tmpInventoryItem.ItemId,
                                                amount: new MyFixedPoint?(myFixedPoint2));
                                            myFixedPoint1 += myFixedPoint3;
                                            amount -= myFixedPoint3;
                                            if ((double) destinationInventory.VolumeFillFactor >= 0.990000009536743 ||
                                                amount <= (MyFixedPoint) 0)
                                            {
                                                __result = myFixedPoint1; 
                                                return false; 
                                            }
                                                
                                        }


                                        if ((double) destinationInventory.VolumeFillFactor >= 0.990000009536743)
                                        {
                                            __result = myFixedPoint1;
                                            return false; 
                                        }
                                }
                            }
                        }
                    }
                }
                else if (!((bool) ReflectionUtils.GetInstanceField(typeof(MyGridConveyorSystem), __instance,
                    "m_isRecomputingGraph")))
                    ReflectionUtils.InvokeInstanceMethod(typeof(MyGridConveyorSystem), __instance,
                        "RecomputeConveyorEndpoints", new object[0]);

                __result = myFixedPoint1;
                return false;
            }
            catch (Exception e)
            {
                Log.Error(e);
                return true;
            }
            return false;
        }

        private static List<IMyConveyorEndpointBlock> PullElements(object conveyorEndpointMapping)
        {
            return (List<IMyConveyorEndpointBlock>) ReflectionUtils.GetInstanceField(conveyorEndpointMapping.GetType(), conveyorEndpointMapping,
                "pullElements");
        }
        
        
        
        private class PullRequestItemSet
        {
            private bool m_all;
            private MyObjectBuilderType? m_obType;
            private MyInventoryConstraint m_constraint;

            public void Clear()
            {
                this.m_all = false;
                this.m_obType = new MyObjectBuilderType?();
                this.m_constraint = (MyInventoryConstraint) null;
            }

            public void Set(bool all)
            {
                this.Clear();
                this.m_all = all;
            }

            public void Set(MyObjectBuilderType? itemTypeId)
            {
                this.Clear();
                this.m_obType = itemTypeId;
            }

            public void Set(MyInventoryConstraint inventoryConstraint)
            {
                this.Clear();
                this.m_constraint = inventoryConstraint;
            }

            public bool Contains(MyDefinitionId itemId) => this.m_all || this.m_obType.HasValue && this.m_obType.Value == itemId.TypeId || this.m_constraint != null && this.m_constraint.Check(itemId);
        }
    }
}