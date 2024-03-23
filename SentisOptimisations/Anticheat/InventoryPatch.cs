using System.Reflection;
using NLog;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Game;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI.Ingame;

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