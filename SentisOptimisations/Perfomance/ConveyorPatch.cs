using System;
using System.Collections.Generic;
using System.Reflection;
using NAPI;
using NLog;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.ModAPI;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRage.Algorithms;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class ConveyorPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        // public static Dictionary<CashedEntry, bool> ConveyerCache = new Dictionary<CashedEntry, bool>();
        public static Dictionary<long, Dictionary<CashedEntry, bool>> ConveyerCacheGrids = new Dictionary<long, Dictionary<CashedEntry, bool>>();
        public static long UncachedCalls = 0;
        public static void Patch(PatchContext ctx)
        {
            var MethodOnBlockAdded = typeof(MyCubeGridSystems).GetMethod
                (nameof(MyCubeGridSystems.OnBlockAdded), BindingFlags.Instance | BindingFlags.Public);


            ctx.GetPattern(MethodOnBlockAdded).Prefixes.Add(
                typeof(ConveyorPatch).GetMethod(nameof(MethodOnBlockAddedPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            
            var MethodUpdateLines = typeof(MyGridConveyorSystem).GetMethod
                (nameof(MyGridConveyorSystem.UpdateLines), BindingFlags.Instance | BindingFlags.Public);
            

            ctx.GetPattern(MethodUpdateLines).Prefixes.Add(
                typeof(ConveyorPatch).GetMethod(nameof(MethodUpdateLinesPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            
            var MethodComputeCanTransfer = typeof(MyGridConveyorSystem).GetMethod
                (nameof(MyGridConveyorSystem.ComputeCanTransfer), BindingFlags.Static | BindingFlags.Public);
            
            ctx.GetPattern(MethodComputeCanTransfer).Prefixes.Add(
                typeof(ConveyorPatch).GetMethod(nameof(ComputeCanTransferPrefix),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            ctx.GetPattern(MethodComputeCanTransfer).Suffixes.Add(
                typeof(ConveyorPatch).GetMethod(nameof(ComputeCanTransferSuffix),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodCanTransferTo = typeof(MyInventory).GetMethod
                ("CanTransferTo", BindingFlags.Instance | BindingFlags.NonPublic);
            
            ctx.GetPattern(MethodCanTransferTo).Prefixes.Add(
                typeof(ConveyorPatch).GetMethod(nameof(CanTransferToPrefix),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            ctx.GetPattern(MethodCanTransferTo).Suffixes.Add(
                typeof(ConveyorPatch).GetMethod(nameof(CanTransferToSuffix),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static void MethodUpdateLinesPatched(
            MyGridConveyorSystem __instance)
        {
            try
            {
                Dictionary<CashedEntry, bool> gridCache;
                if (ConveyerCacheGrids.TryGetValue(__instance.ResourceSink.Grid.EntityId, out gridCache))
                {
                    gridCache.Clear();
                }
            }
            catch (Exception e)
            {
                
            }

        }

        private static bool CanTransferToPrefix(
                    MyInventory __instance, MyInventory dstInventory, MyDefinitionId? itemType,
            ref bool __result)
        {
            if (!SentisOptimisationsPlugin.Config.ConveyorCacheEnabled)
            {
                return true;
            }
            IMyConveyorEndpointBlock owner1 = __instance.Owner as IMyConveyorEndpointBlock;
            IMyConveyorEndpointBlock owner2 = dstInventory.Owner as IMyConveyorEndpointBlock;
            var startBlock = owner1 as IMyCubeBlock;
            if (startBlock == null)
            {
                return false;
            }

            var endBlock = owner2 as IMyCubeBlock;
            if (endBlock == null)
            {
                return false;
            }
            var cashedEntity = new CashedEntry(startBlock.EntityId, endBlock.EntityId, itemType);
            Dictionary<CashedEntry, bool> cacheByGrid;

            if (ConveyerCacheGrids.TryGetValue(endBlock.CubeGrid.EntityId, out cacheByGrid))
            {
                bool cachedResult;
                if (cacheByGrid.TryGetValue(cashedEntity, out cachedResult))
                {
                    __result = cachedResult;
                    return false;
                }
            }
            return true;
        }
        
        private static void CanTransferToSuffix(
            MyInventory __instance, MyInventory dstInventory, MyDefinitionId? itemType,
            ref bool __result)
        {
            if (!SentisOptimisationsPlugin.Config.ConveyorCacheEnabled)
            {
                return;
            }
            UncachedCalls++;
            IMyConveyorEndpointBlock owner1 = __instance.Owner as IMyConveyorEndpointBlock;
            IMyConveyorEndpointBlock owner2 = dstInventory.Owner as IMyConveyorEndpointBlock;
            var startBlock = owner1 as IMyCubeBlock;
            if (startBlock == null)
            {
                return;
            }

            var endBlock = owner2 as IMyCubeBlock;
            if (endBlock == null)
            {
                return;
            }
            var cashedEntity = new CashedEntry(startBlock.EntityId, endBlock.EntityId, itemType);
            Dictionary<CashedEntry, bool> cacheByGrid;
            if (ConveyerCacheGrids.TryGetValue(endBlock.CubeGrid.EntityId, out cacheByGrid))
            {
                cacheByGrid[cashedEntity] = __result;
                return;
            }

            cacheByGrid = new Dictionary<CashedEntry, bool>();
            cacheByGrid[cashedEntity] = __result;
            ConveyerCacheGrids[endBlock.CubeGrid.EntityId] = cacheByGrid;
        }
        
        private static bool ComputeCanTransferPrefix(
            IMyConveyorEndpointBlock start,
            IMyConveyorEndpointBlock end,
            MyDefinitionId? itemId,
            ref bool __result)
        {
            if (!SentisOptimisationsPlugin.Config.ConveyorCacheEnabled)
            {
                return true;
            }
            var startBlock = start as IMyCubeBlock;
            if (startBlock == null)
            {
                return false;
            }

            var endBlock = end as IMyCubeBlock;
            if (endBlock == null)
            {
                return false;
            }
            var cashedEntity = new CashedEntry(startBlock.EntityId, endBlock.EntityId, itemId);
            Dictionary<CashedEntry, bool> cacheByGrid;

            if (ConveyerCacheGrids.TryGetValue(endBlock.CubeGrid.EntityId, out cacheByGrid))
            {
                bool cachedResult;
                if (cacheByGrid.TryGetValue(cashedEntity, out cachedResult))
                {
                    __result = cachedResult;
                    return false;
                }
            }
            return true;
        }
        
        private static void ComputeCanTransferSuffix(
            IMyConveyorEndpointBlock start,
            IMyConveyorEndpointBlock end,
            MyDefinitionId? itemId,
            ref bool __result)
        {
            if (!SentisOptimisationsPlugin.Config.ConveyorCacheEnabled)
            {
                return;
            }
            UncachedCalls++;
            var startBlock = start as IMyCubeBlock;
            if (startBlock == null)
            {
                return;
            }

            var endBlock = end as IMyCubeBlock;
            if (endBlock == null)
            {
                return;
            }
            var cashedEntity = new CashedEntry(startBlock.EntityId, endBlock.EntityId, itemId);
            Dictionary<CashedEntry, bool> cacheByGrid;
            if (ConveyerCacheGrids.TryGetValue(endBlock.CubeGrid.EntityId, out cacheByGrid))
            {
                cacheByGrid[cashedEntity] = __result;
                return;
            }

            cacheByGrid = new Dictionary<CashedEntry, bool>();
            cacheByGrid[cashedEntity] = __result;
            ConveyerCacheGrids[endBlock.CubeGrid.EntityId] = cacheByGrid;
        }
        

        private static void MethodOnBlockAddedPatched(MyCubeGridSystems __instance, MySlimBlock block)
        {
            if (VoxelsPatch.Protectors == null)
            {
                if (block.FatBlock is MyUpgradeModule)
                {
                    foreach (var myEntityComponent in block.FatBlock.Components)
                    {
                        if (myEntityComponent.GetType().Name.Equals("NanoBotSuppressor"))
                        {
                            var fieldProtectors = myEntityComponent.GetType().GetField("Protectors");
                            if (fieldProtectors == null)
                            {
                                Log.Error("No voxel protector support");
                                VoxelsPatch.Protectors = new HashSet<IMyUpgradeModule>();
                                return;
                            }
                            VoxelsPatch.Protectors = (HashSet<IMyUpgradeModule>)fieldProtectors.GetValue(null);
                        }
                    }
                }
            }
        }

        public class CashedEntry
        {
            private long _startBlockEntityId;
            private long _endBlockEntityId;
            private MyDefinitionId? _itemId;

            public long StartBlockEntityId
            {
                get => _startBlockEntityId;
                set => _startBlockEntityId = value;
            }

            public long EndBlockEntityId
            {
                get => _endBlockEntityId;
                set => _endBlockEntityId = value;
            }

            public MyDefinitionId? ItemId
            {
                get => _itemId;
                set => _itemId = value;
            }

            public CashedEntry(long startBlockEntityId, long endBlockEntityId, MyDefinitionId? itemId)
            {
                _startBlockEntityId = startBlockEntityId;
                _endBlockEntityId = endBlockEntityId;
                _itemId = itemId;
            }

            public override bool Equals(object obj)
            {
                if (obj == null)
                {
                    return false;
                }

                if (obj is CashedEntry)
                {
                    if (((CashedEntry)obj).ItemId == null)
                    {
                        if (this.ItemId != null)
                        {
                            return false;
                        }
                        return ((CashedEntry)obj).StartBlockEntityId == this.StartBlockEntityId &&
                               ((CashedEntry)obj).EndBlockEntityId == this.EndBlockEntityId;
                    }

                    return ((CashedEntry)obj).StartBlockEntityId == this.StartBlockEntityId &&
                           ((CashedEntry)obj).EndBlockEntityId == this.EndBlockEntityId &&
                           ((CashedEntry)obj).ItemId.Equals(this.ItemId);
                }

                return false;
            }

            public override int GetHashCode()
            {
                int hashcode = ItemId != null ? ItemId.GetHashCode() : 0;
                hashcode = (int)(hashcode + (StartBlockEntityId & 0xFFFFFFFF) + (EndBlockEntityId & 0xFFFFFFFF));
                return hashcode;
            }
        }
    }
}