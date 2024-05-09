using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NAPI;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Inventory;
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
    }

    private static bool UpdateProduction(MyAssembler __instance, uint framesFromLastTrigger, bool forceUpdate = false)
    {
        if (framesFromLastTrigger < 1200)
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
                // __instance.IsProducing = __instance.IsWorking && !__instance.IsQueueEmpty && __instance.CurrentState == MyAssembler.StateEnum.Ok;
            });
        }
        catch (Exception e)
        {
            SentisOptimisationsPlugin.Log.Error(e, "UpdateProduction compensation exception");
            return true;
        }
        return false;
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