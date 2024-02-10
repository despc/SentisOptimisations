using System;
using System.Reflection;
using NLog.Fluent;
using Sandbox.Engine.Physics;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Inventory;
using Sandbox.Game.EntityComponents;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game;
using VRage.Game.Components;
using VRage.ModAPI;
using VRage.ObjectBuilders;

namespace SentisOptimisationsPlugin.Freezer;

[PatchShim]
public static class FreezerPatches
{
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