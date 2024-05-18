using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NAPI;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Inventory;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations.DelayedLogic;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Sync;
using VRage.Utils;

namespace SentisOptimisationsPlugin.Freezer;

[PatchShim]
public static class FreezerPatches
{
    private static PropertyInfo IsProducingProp =
        typeof(MyAssembler).GetProperty("IsProducing",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static PropertyInfo CurrentStateProp =
        typeof(MyAssembler).GetProperty("CurrentState",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    public static void Patch(PatchContext ctx)
    {
        var MethodAddItems = typeof(MyInventory).GetMethod
        ("AddItems",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, CallingConventions.Any,
            new[] { typeof(MyFixedPoint), typeof(MyObjectBuilder_Base), typeof(uint?), typeof(int) }, null);


        ctx.GetPattern(MethodAddItems).Prefixes.Add(
            typeof(FreezerPatches).GetMethod(nameof(AddItemsPatched),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

        var MethodOnMotionDynamic = typeof(MyPhysicsBody).GetMethod
            ("OnMotionDynamic", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);


        ctx.GetPattern(MethodOnMotionDynamic).Prefixes.Add(
            typeof(FreezerPatches).GetMethod(nameof(OnMotionDynamicPatched),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

        var MethodRefreshCustomInfo = typeof(MyTerminalBlock).GetMethod
            ("RefreshCustomInfo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);


        ctx.GetPattern(MethodRefreshCustomInfo).Prefixes.Add(
            typeof(FreezerPatches).GetMethod(nameof(RefreshCustomInfoPatched),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

        var MethodUpdateProduction = typeof(MyAssembler).GetMethod
            ("UpdateProduction", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        ctx.GetPattern(MethodUpdateProduction).Prefixes.Add(
            typeof(FreezerPatches).GetMethod(nameof(UpdateProduction),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        
        var MethodGetComponentsFromConveyor = typeof(MyAssembler).GetMethod
            ("GetComponentsFromConveyor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        ctx.GetPattern(MethodGetComponentsFromConveyor).Prefixes.Add(
            typeof(FreezerPatches).GetMethod(nameof(GetComponentsFromConveyorPatch),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
    }

    private static bool GetComponentsFromConveyorPatch(MyAssembler __instance)
    {
        if (__instance.InputInventory.VolumeFillFactor >= 0.99)
            return false;
        MyTimerComponent timer =
            (MyTimerComponent)__instance.easyGetField("m_timer", typeof(MyFunctionalBlock));
        var timerFramesFromLastTrigger = timer.FramesFromLastTrigger;
        DelayedProcessor.Instance.AddDelayedAction(DateTime.Now, () =>
        {
            try
            {
                AsyncCollectAssemblerRequiredItems(__instance, timerFramesFromLastTrigger);
            }
            catch (Exception e)
            {
                //
            }
        });

        return false;
    }

    private static void AsyncCollectAssemblerRequiredItems(MyAssembler __instance, uint timerFramesFromLastTrigger)
    {
        float num1 = 0.0f;
        List<MyProductionBlock.QueueItem> m_queue =
            (List<MyProductionBlock.QueueItem>)__instance.easyGetField("m_queue");
        List<MyTuple<MyFixedPoint, MyBlueprintDefinitionBase.Item>> m_requiredComponents =
            new List<MyTuple<MyFixedPoint, MyBlueprintDefinitionBase.Item>>();
        foreach (var queueItem in m_queue)
        {
            float num3 = 5f - num1;
            float num4 = MySession.Static.AssemblerSpeedMultiplier *
                         (((MyAssemblerDefinition)__instance.BlockDefinition).AssemblySpeed +
                          __instance.UpgradeValues["Productivity"]);
            int num5 = 1;
            if (queueItem.Blueprint.BaseProductionTimeInSeconds / (double)num4 < num3)
                num5 = Math.Min((int)queueItem.Amount,
                    Convert.ToInt32(Math.Ceiling(num3 /
                                                 (queueItem.Blueprint.BaseProductionTimeInSeconds /
                                                  (double)num4))));
            num1 += num5 * queueItem.Blueprint.BaseProductionTimeInSeconds / num4;
            MyFixedPoint myFixedPoint1 =
                (MyFixedPoint)(1f / __instance.GetEfficiencyMultiplierForBlueprint(queueItem.Blueprint));
            foreach (MyBlueprintDefinitionBase.Item prerequisite in queueItem.Blueprint.Prerequisites)
            {
                MyFixedPoint myFixedPoint2 = prerequisite.Amount * myFixedPoint1;
                MyFixedPoint myFixedPoint3 = myFixedPoint2 * num5;
                bool flag2 = false;
                for (int index = 0; index < m_requiredComponents.Count; ++index)
                {
                    MyBlueprintDefinitionBase.Item obj = m_requiredComponents[index].Item2;
                    if (obj.Id == prerequisite.Id)
                    {
                        obj.Amount += myFixedPoint3;
                        MyFixedPoint myFixedPoint4 = m_requiredComponents[index].Item1 + myFixedPoint2;
                        m_requiredComponents[index] =
                            MyTuple.Create(myFixedPoint4, obj);
                        flag2 = true;
                        // break;
                    }
                }

                if (!flag2)
                    m_requiredComponents.Add(MyTuple.Create(myFixedPoint2, new MyBlueprintDefinitionBase.Item()
                    {
                        Amount = myFixedPoint3,
                        Id = prerequisite.Id
                    }));
            }

            foreach (MyTuple<MyFixedPoint, MyBlueprintDefinitionBase.Item> requiredComponent in
                     m_requiredComponents)
            {
                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    try
                    {
                        MyBlueprintDefinitionBase.Item obj = requiredComponent.Item2;
                        MyFixedPoint itemAmount =
                            __instance.InputInventory.GetItemAmount(obj.Id, MyItemFlags.None, false);

                        MyFixedPoint myFixedPoint = (obj.Amount * (timerFramesFromLastTrigger / 60)) - itemAmount;
                        if (!(myFixedPoint <= 0))
                        {
                            var fixedPoint = __instance.CubeGrid.GridSystems.ConveyorSystem.PullItem(obj.Id,
                                new MyFixedPoint?(myFixedPoint),
                                __instance, __instance.InputInventory, false, false);
                        }
                    }
                    catch (Exception e)
                    {
                        SentisOptimisationsPlugin.Log.Error(e, "Assembler collect items exception");
                    }
                });
            }
        }

        if (!__instance.IsSlave || __instance.RepeatEnabled)
            return;
        float remainingTime = 5f - num1;
        if (remainingTime <= 0.0)
            return;
        __instance.easyCallMethod("GetItemFromOtherAssemblers", new object[] { remainingTime });
    }

    private static bool UpdateProduction(MyAssembler __instance, uint framesFromLastTrigger, bool forceUpdate = false)
    {
        if (framesFromLastTrigger < 3601)
        {
            return true;
        }

        if (__instance.DisassembleEnabled)
        {
            return true;
        }

        if (__instance is MySurvivalKit)
        {
            return true;
        }

        try
        {
            Dictionary<MyBlueprintDefinitionBase, int> assemblingCount =
                new Dictionary<MyBlueprintDefinitionBase, int>();
            DelayedProcessor.Instance.AddDelayedAction(DateTime.Now, () =>
            {
                try
                {
                    AsyncUpdateAssemblerProduction(__instance, framesFromLastTrigger, forceUpdate, assemblingCount);
                }
                catch (Exception e)
                {
                    //
                }
            });
        }
        catch (Exception e)
        {
            SentisOptimisationsPlugin.Log.Error(e, "UpdateProduction compensation exception");
            return true;
        }
        return false;
    }

    private static void AsyncUpdateAssemblerProduction(MyAssembler __instance, uint framesFromLastTrigger,
        bool forceUpdate, Dictionary<MyBlueprintDefinitionBase, int> assemblingCount)
    {
        __instance.UpdateCurrentState();
        uint? m_realProductionStart =
            (uint?)__instance.easyGetField("m_realProductionStart", typeof(MyProductionBlock));
        int idx = 0;
        float num1;
        if (m_realProductionStart.HasValue)
        {
            num1 = (float)Math.Round((framesFromLastTrigger - m_realProductionStart.Value) *
                                     16.66666603088379);
            __instance.easySetField("m_realProductionStart", new uint?(), typeof(MyProductionBlock));
        }
        else
            num1 = (float)Math.Round((double)framesFromLastTrigger * 16.66666603088379);

        bool flag = false;
        Sync<int, SyncDirection.FromServer> m_currentItemIndex =
            (Sync<int, SyncDirection.FromServer>)__instance.easyGetField("m_currentItemIndex");
        List<MyProductionBlock.QueueItem> m_queue =
            (List<MyProductionBlock.QueueItem>)__instance.easyGetField("m_queue");
        while ((num1 > 0.0 || forceUpdate && !flag) && idx < m_queue.Count)
        {
            flag = true;
            if (__instance.IsQueueEmpty)
            {
                __instance.CurrentProgress = 0.0f;
                IsProducingProp.GetSetMethod(true).Invoke(__instance, new object[] { false });
                // __instance.IsProducing = false;
                return;
            }

            MyProductionBlock.QueueItem? m_currentQueueItem =
                (MyProductionBlock.QueueItem?)__instance.easyGetField("m_currentQueueItem");

            if (!m_currentQueueItem.HasValue)
            {
                __instance.easySetField("m_currentQueueItem", __instance.TryGetQueueItem(idx));
                m_currentQueueItem =
                    (MyProductionBlock.QueueItem?)__instance.easyGetField("m_currentQueueItem");
            }
                        
            MyBlueprintDefinitionBase blueprint = m_currentQueueItem.Value.Blueprint;
            var instanceCurrentState = __instance.easyCallMethod("CheckInventory", new object[] { blueprint });

            CurrentStateProp.GetSetMethod(true).Invoke(__instance, new object[] { instanceCurrentState });
            // __instance.CurrentState = instanceCurrentState;

            if (__instance.CurrentState != MyAssembler.StateEnum.Ok)
            {
                ++idx;
                __instance.easySetField("m_currentQueueItem", new MyProductionBlock.QueueItem?());
            }
            else
            {
                float blueprintProductionTime = CalculateBlueprintProductionTime(blueprint, __instance);
                float num2 =
                    (float)Math.Round((1.0 - __instance.CurrentProgress) * blueprintProductionTime);
                if ((double)num1 >= num2)
                {
                    if (__instance.RepeatEnabled)
                        __instance.InsertQueueItemRequest(-1, blueprint);

                    if (assemblingCount.TryGetValue(blueprint, out var count))
                    {
                        assemblingCount[blueprint] = ++count;
                    }
                    else
                    {
                        assemblingCount[blueprint] = 1;
                    }

                    m_currentItemIndex.Value = __instance.CurrentItemIndexServer;
                    __instance.RemoveQueueItemRequest(m_queue.IndexOf(m_currentQueueItem.Value), 1);
                    __instance.easySetField("m_currentQueueItem", new MyProductionBlock.QueueItem?());

                    num1 -= num2;
                    __instance.CurrentProgress = 0.0f;
                    __instance.easySetField("m_currentQueueItem", new MyProductionBlock.QueueItem?());
                }
                else
                {
                    __instance.CurrentProgress += num1 / blueprintProductionTime;
                    num1 = 0.0f;
                }
            }
        }

        foreach (var assemblingEntry in assemblingCount)
        {
            var bp = assemblingEntry.Key;
            var count = assemblingEntry.Value;
            FreezeLogic.CompensationLogs($"Compensate Assembler, build {count} items of {bp.DisplayNameText}");
            FinishAssembling(bp, count, __instance);
        }

        Thread.Sleep(32);
        if (__instance.CurrentState != MyAssembler.StateEnum.Ok || __instance.CurrentItemIndexServer != -1)
            m_currentItemIndex.Value = __instance.CurrentItemIndexServer;
        IsProducingProp.GetSetMethod(true).Invoke(__instance,
            new object[]
            {
                __instance.IsWorking && !__instance.IsQueueEmpty &&
                __instance.CurrentState == MyAssembler.StateEnum.Ok
            });
    }

    private static void FinishAssembling(MyBlueprintDefinitionBase blueprint, int count, MyAssembler assembler)
    {
        MyFixedPoint myFixedPoint = (MyFixedPoint)(1f / assembler.GetEfficiencyMultiplierForBlueprint(blueprint));
        bool allPrereqExists = true;
        Dictionary<MyInventory, KeyValuePair<MyFixedPoint?, MyDefinitionId>> inventoriesToRemove =
            new Dictionary<MyInventory, KeyValuePair<MyFixedPoint?, MyDefinitionId>>();

        for (int index = 0; index < blueprint.Prerequisites.Length; ++index)
        {
            MyBlueprintDefinitionBase.Item prerequisite = blueprint.Prerequisites[index];
            if (assembler.InputInventory.ContainItems(prerequisite.Amount * myFixedPoint * count, prerequisite.Id))
            {
                inventoriesToRemove[assembler.InputInventory] =
                    new KeyValuePair<MyFixedPoint?, MyDefinitionId>(prerequisite.Amount * myFixedPoint * count,
                        prerequisite.Id);
                continue;
            }

            if (HasItemsInOtherBlocks(assembler, prerequisite, myFixedPoint, inventoriesToRemove, count))
            {
                continue;
            }

            allPrereqExists = false;
        }

        if (!allPrereqExists)
        {
            return;
        }

        MyAPIGateway.Utilities.InvokeOnGameThread(() =>
        {
            try
            {
                foreach (var i2r in inventoriesToRemove)
                {
                    i2r.Key.RemoveItemsOfType((MyFixedPoint)i2r.Value.Key, i2r.Value.Value);
                }

                foreach (MyBlueprintDefinitionBase.Item result in blueprint.Results)
                {
                    MyObjectBuilder_PhysicalObject newObject =
                        (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializerKeen.CreateNewObject(result.Id.TypeId,
                            result.Id.SubtypeName);
                    assembler.OutputInventory.AddItems(result.Amount * count, (MyObjectBuilder_Base)newObject);
                    if (MyVisualScriptLogicProvider.NewItemBuilt != null)
                        MyVisualScriptLogicProvider.NewItemBuilt(assembler.EntityId, assembler.CubeGrid.EntityId,
                            assembler.Name, assembler.CubeGrid.Name, newObject.TypeId.ToString(), newObject.SubtypeName,
                            result.Amount.ToIntSafe() * count);
                }
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "Compensate assembler exception");
            }
        });
    }

    private static bool HasItemsInOtherBlocks(MyAssembler assembler, MyBlueprintDefinitionBase.Item prerequisite,
        MyFixedPoint myFixedPoint,
        Dictionary<MyInventory, KeyValuePair<MyFixedPoint?, MyDefinitionId>> inventoriesToRemove, int count)
    {
        foreach (var myCubeBlock in assembler.CubeGrid.GetFatBlocks())
        {
            if (myCubeBlock.HasInventory)
            {
                if (myCubeBlock.GetInventory()
                    .ContainItems(prerequisite.Amount * myFixedPoint * count, prerequisite.Id))
                {
                    inventoriesToRemove[myCubeBlock.GetInventory()] =
                        new KeyValuePair<MyFixedPoint?, MyDefinitionId>(prerequisite.Amount * myFixedPoint * count,
                            prerequisite.Id);
                    return true;
                }
            }
        }

        return false;
    }

    private static float CalculateBlueprintProductionTime(MyBlueprintDefinitionBase currentBlueprint,
        MyAssembler assembler)
    {
        return (float)Math.Round((double)currentBlueprint.BaseProductionTimeInSeconds * 1000.0
                                 / ((double)MySession.Static.AssemblerSpeedMultiplier * (double)
                                    ((MyAssemblerDefinition)assembler.BlockDefinition).AssemblySpeed +
                                    (double)assembler.UpgradeValues["Productivity"]));
    }

    private static bool RefreshCustomInfoPatched(MyTerminalBlock __instance)
    {
        if (!SentisOptimisationsPlugin.Config.FreezerEnabled)
        {
            return true;
        }

        var entityId = __instance.CubeGrid.EntityId;

        if (FreezeLogic.FrozenGrids.Contains(entityId))
        {
            return false;
        }

        return true;
    }

    private static bool OnMotionDynamicPatched(MyPhysicsBody __instance)
    {
        if (!SentisOptimisationsPlugin.Config.FreezePhysics)
        {
            return true;
        }

        IMyEntity entity = __instance.Entity;
        if (entity == null)
            return false;
        if (FreezeLogic.FrozenGrids.Contains(entity.EntityId))
        {
            return false;
        }

        return true;
    }

    private static bool AddItemsPatched(MyInventory __instance,
        ref MyFixedPoint amount,
        MyObjectBuilder_Base objectBuilder)
    {
        IMyEntity entity = __instance.Entity;

        if (!(entity is MyProductionBlock))
        {
            return true;
        }

        if (amount == 0)
            return false;

        MyDefinitionId id = objectBuilder.GetId();
        if (__instance.CanItemsBeAdded(amount, id))
        {
            return true;
        }

        MyInventoryItemAdapter inventoryItemAdapter = MyInventoryItemAdapter.Static;
        inventoryItemAdapter.Adapt(id);
        var freeVolume = __instance.MaxVolume - __instance.CurrentVolume;
        var freeMass = __instance.MaxMass - __instance.CurrentMass;

        var maxVolumeCanBeAdded = freeVolume * (MyFixedPoint)0.75;
        var maxMassCanBeAdded = freeMass * (MyFixedPoint)0.75;

        int itemsByVolumeCanBeAdded = (int)((int)maxVolumeCanBeAdded / inventoryItemAdapter.Volume);
        itemsByVolumeCanBeAdded = itemsByVolumeCanBeAdded < 0 ? int.MaxValue : itemsByVolumeCanBeAdded;
        var itemsByMassCanBeAdded = (int)((int)maxMassCanBeAdded / inventoryItemAdapter.Mass);
        itemsByMassCanBeAdded = itemsByMassCanBeAdded < 0 ? int.MaxValue : itemsByMassCanBeAdded;

        var itemsCountCanBeAdded = Math.Min(itemsByVolumeCanBeAdded, itemsByMassCanBeAdded);

        var amountToAddToAnotherInventory = amount - itemsCountCanBeAdded;
        if (amountToAddToAnotherInventory < 0)
        {
            SentisOptimisationsPlugin.Log.Error("amountToAddToAnotherInventory - " + amountToAddToAnotherInventory);
            return true;
        }

        amount = itemsCountCanBeAdded;

        var myProductionBlock = (MyProductionBlock)entity;
        FreezeLogic.CompensationLogs("Compensated items cant be added to " + myProductionBlock.DisplayNameText +
                                     " inventory, move " + amountToAddToAnotherInventory + " items of " +
                                     id.SubtypeName + " to container");

        foreach (var myCargoContainer in myProductionBlock.CubeGrid.GetFatBlocks<MyCargoContainer>())
        {
            if (myCargoContainer.GetInventory().CanItemsBeAdded(amountToAddToAnotherInventory, id))
            {
                myCargoContainer.GetInventory().AddItems(amountToAddToAnotherInventory, objectBuilder);

                FreezeLogic.CompensationLogs("Move items to " + myCargoContainer.DisplayNameText +
                                             " inventory, count - "
                                             + amountToAddToAnotherInventory + " item - " + id.SubtypeName +
                                             " on grid " + myProductionBlock.CubeGrid.DisplayName);
                return true;
            }
        }

        return true;
    }
}