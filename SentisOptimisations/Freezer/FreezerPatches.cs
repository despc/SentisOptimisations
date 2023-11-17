using System;
using System.Reflection;
using System.Threading.Tasks;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Inventory;
using Sandbox.ModAPI;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game;
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
            new[]{typeof(MyFixedPoint), typeof(MyObjectBuilder_Base), typeof(uint?), typeof(int)}, null);


        ctx.GetPattern(MethodAddItems).Prefixes.Add(
            typeof(FreezerPatches).GetMethod(nameof(AddItemsPatched),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
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

        int itemsByVolumeCanBeAdded =  Math.Abs((int)((int) maxVolumeCanBeAdded / inventoryItemAdapter.Volume));
        int itemsByMassCanBeAdded = Math.Abs((int)((int) maxMassCanBeAdded / inventoryItemAdapter.Mass));

        var itemsCountCanBeAdded = Math.Min(itemsByVolumeCanBeAdded, itemsByMassCanBeAdded);
        var amountToAddToAnotherInventory = amount - itemsCountCanBeAdded;
        amount = itemsCountCanBeAdded;
        
        var myProductionBlock = (MyProductionBlock)entity;
        FreezeLogic.CompensationLogs("Compensated items cant be added to " + myProductionBlock.DisplayNameText +
                                     " inventory, move " + amountToAddToAnotherInventory + " items of " + id.SubtypeName + " to container");
        AddToAnotherInventoryAsync(amountToAddToAnotherInventory, objectBuilder, myProductionBlock);
        return true;
    }

    private static void AddToAnotherInventoryAsync(MyFixedPoint amountToAddToAnotherInventory, MyObjectBuilder_Base objectBuilder,
        MyProductionBlock myProductionBlock)
    {
        Task.Run(() =>
        {
            MyDefinitionId id = objectBuilder.GetId();
            foreach (var myCargoContainer in myProductionBlock.CubeGrid.GetFatBlocks<MyCargoContainer>())
            {
                if (myCargoContainer.GetInventory().CanItemsBeAdded(amountToAddToAnotherInventory, id))
                {
                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    {
                        myCargoContainer.GetInventory().AddItems(amountToAddToAnotherInventory, objectBuilder);
                    });
                    FreezeLogic.CompensationLogs("Move items to " + myCargoContainer.DisplayNameText + " inventory, count - " 
                                                 + amountToAddToAnotherInventory + " item - " + id.SubtypeName +
                                                 " on grid " + myProductionBlock.CubeGrid.DisplayName);
                    return;
                }
            }
        });
    }
}