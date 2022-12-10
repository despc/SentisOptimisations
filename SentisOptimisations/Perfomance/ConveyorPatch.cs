using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.ModAPI;
using SentisOptimisations;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Algorithms;
using VRage.Game;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class ConveyorPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static CancellationTokenSource CancellationTokenSource { get; set; }

        public static ConcurrentDictionary<long, ConcurrentDictionary<Key, bool>> ConveyourCache =
            new ConcurrentDictionary<long, ConcurrentDictionary<Key, bool>>();

        private static bool IsConveyorLarge(IMyPathEdge<IMyConveyorEndpoint> conveyorLine) => !(conveyorLine is MyConveyorLine) || (conveyorLine as MyConveyorLine).Type == MyObjectBuilder_ConveyorLine.LineType.LARGE_LINE;


        private static Predicate<IMyPathEdge<IMyConveyorEndpoint>> IsConveyorLargePredicate = new Predicate<IMyPathEdge<IMyConveyorEndpoint>>(IsConveyorLarge);
        private static Predicate<IMyConveyorEndpoint> IsAccessAllowedPredicate = new Predicate<IMyConveyorEndpoint>(
            delegate(IMyConveyorEndpoint endpoint)
            {
                return (bool) ReflectionUtils.InvokeStaticMethod(typeof(MyGridConveyorSystem), "IsAccessAllowed",
                    new object[] {endpoint});});
        
        public static long UncachedCalls = 0;
        public static Random r = new Random();
        
        
        private static MyPathFindingSystem<IMyConveyorEndpoint> Pathfinding
        {
            get
            {
                if (ReflectionUtils.GetPrivateStaticField(typeof(MyGridConveyorSystem), "m_pathfinding") == null)
                {
                    ReflectionUtils.SetPrivateStaticField(typeof(MyGridConveyorSystem), "m_pathfinding",
                        new MyPathFindingSystem<IMyConveyorEndpoint>());
                }
                return (MyPathFindingSystem<IMyConveyorEndpoint>) ReflectionUtils.GetPrivateStaticField(typeof(MyGridConveyorSystem), "m_pathfinding");
            }
        }
        
        public static void Patch(PatchContext ctx)
        {
            var MethodOnBlockAdded = typeof(MyCubeGridSystems).GetMethod
                (nameof(MyCubeGridSystems.OnBlockAdded), BindingFlags.Instance | BindingFlags.Public);


            ctx.GetPattern(MethodOnBlockAdded).Prefixes.Add(
                typeof(ConveyorPatch).GetMethod(nameof(MethodOnBlockAddedPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodOnBlockRemoved = typeof(MyCubeGridSystems).GetMethod
                (nameof(MyCubeGridSystems.OnBlockRemoved), BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodOnBlockRemoved).Prefixes.Add(
                typeof(ConveyorPatch).GetMethod(nameof(MethodOnBlockRemovedPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            // Кэш конвеера

            var MethodFlagForRecomputation = typeof(MyGridConveyorSystem).GetMethod
                (nameof(MyGridConveyorSystem.FlagForRecomputation), BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodFlagForRecomputation).Prefixes.Add(
                typeof(ConveyorPatch).GetMethod(nameof(MethodFlagForRecomputationPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));


            var MethodComputeCanTransfer = typeof(MyGridConveyorSystem).GetMethod
                (nameof(MyGridConveyorSystem.ComputeCanTransfer), BindingFlags.Static | BindingFlags.Public);

            ctx.GetPattern(MethodComputeCanTransfer).Prefixes.Add(
                typeof(ConveyorPatch).GetMethod(nameof(ComputeCanTransferPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodComputeCanTransferPost = typeof(MyGridConveyorSystem).GetMethod
                (nameof(MyGridConveyorSystem.ComputeCanTransfer), BindingFlags.Static | BindingFlags.Public);

            ctx.GetPattern(MethodComputeCanTransferPost).Suffixes.Add(
                typeof(ConveyorPatch).GetMethod(nameof(ComputeCanTransferPatchedPost),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

        }

        private static bool ComputeCanTransferPatched(IMyConveyorEndpointBlock start,
            IMyConveyorEndpointBlock end, MyDefinitionId? itemId, ref bool __result)
        {
            try
            {
                if (!SentisOptimisationsPlugin.Config.ConveyorCacheEnabled)
                {
                    return true;
                }
                if (start is MyCubeBlock && end is MyCubeBlock)
                {
                    var startEntityId = ((MyCubeBlock) start).EntityId;
                    var endEntityId = ((MyCubeBlock) end).EntityId;
                    var cubeGridEntityId = ((MyCubeBlock) end).CubeGrid.EntityId;
                    var key = new Key(startEntityId, endEntityId, itemId);
                    ConcurrentDictionary<Key, bool> cacheEntry;
                    if (ConveyourCache.TryGetValue(cubeGridEntityId, out cacheEntry))
                    {
                        if (cacheEntry.TryGetValue(key, out __result))
                        {
                            return false;
                        }
                    }
                }
                UncachedCalls += 1;
            }
            catch (Exception e)
            {
                Log.Error(e);
            }

            __result = ComputeCanTransferNative(start, end, itemId);
            return false;
        }
        
        public static bool ComputeCanTransferNative(
            IMyConveyorEndpointBlock start,
            IMyConveyorEndpointBlock end,
            MyDefinitionId? itemId)
        {
            List<IMyConveyorEndpoint> m_reachableBuffer = new List<IMyConveyorEndpoint>();
            lock (Pathfinding)
            {
                ReflectionUtils.InvokeStaticMethod(typeof(MyGridConveyorSystem), "SetTraversalPlayerId",
                    new object[] {start.ConveyorEndpoint.CubeBlock.OwnerId});
                // MyGridConveyorSystem.SetTraversalPlayerId(start.ConveyorEndpoint.CubeBlock.OwnerId);
                if (itemId.HasValue)
                {
                    ReflectionUtils.InvokeStaticMethod(typeof(MyGridConveyorSystem),
                        "SetTraversalInventoryItemDefinitionId",
                        new object[] {itemId.Value});
                    // MyGridConveyorSystem.SetTraversalInventoryItemDefinitionId(itemId.Value);
                }

                else
                {
                    // MyGridConveyorSystem.SetTraversalInventoryItemDefinitionId();
                    MyDefinitionId item = default(MyDefinitionId);
                    ReflectionUtils.InvokeStaticMethod(typeof(MyGridConveyorSystem),
                        "SetTraversalInventoryItemDefinitionId",
                        new object[] {item});
                }

                Predicate<IMyPathEdge<IMyConveyorEndpoint>> edgeTraversable =
                    (Predicate<IMyPathEdge<IMyConveyorEndpoint>>) null;
                if (itemId.HasValue && (bool) ReflectionUtils.InvokeStaticMethod(typeof(MyGridConveyorSystem),
                    "NeedsLargeTube",
                    new object[] {itemId.Value}))
                    edgeTraversable = IsConveyorLargePredicate;
                Pathfinding.FindReachable(start.ConveyorEndpoint, m_reachableBuffer,
                    (Predicate<IMyConveyorEndpoint>) (b => b != null && b.CubeBlock == end),
                    IsAccessAllowedPredicate, edgeTraversable);
            }

            return (uint) m_reachableBuffer.Count > 0U;
        }

        private static void ComputeCanTransferPatchedPost(IMyConveyorEndpointBlock start,
            IMyConveyorEndpointBlock end, bool __result, MyDefinitionId? itemId)
        {
            try
            {
                if (!SentisOptimisationsPlugin.Config.ConveyorCacheEnabled)
                {
                    return;
                }

                if (start is MyCubeBlock && end is MyCubeBlock)
                {
                    var startEntityId = ((MyCubeBlock) start).EntityId;
                    var endEntityId = ((MyCubeBlock) end).EntityId;
                    var cubeGridEntityId = ((MyCubeBlock) end).CubeGrid.EntityId;
                    ConcurrentDictionary<Key, bool> cacheEntry;
                    var key = new Key(startEntityId, endEntityId, itemId);
                    if (ConveyourCache.TryGetValue(cubeGridEntityId, out cacheEntry))
                    {
                        cacheEntry[key] = __result;
                        return;
                    }

                    cacheEntry = new ConcurrentDictionary<Key, bool>();
                    cacheEntry[key] = __result;
                    ConveyourCache[cubeGridEntityId] = cacheEntry;
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private static bool MethodOnBlockAddedPatched(MyCubeGridSystems __instance, MySlimBlock block)
        {
            MyCubeGrid m_cubeGrid =
                (MyCubeGrid) ReflectionUtils.GetInstanceField(__instance.GetType(), __instance, "m_cubeGrid");


            if (VoxelsPatch.Protectors == null)
            {
                if (block.FatBlock is MyUpgradeModule)
                {
                    foreach (var myEntityComponent in block.FatBlock.Components)
                    {
                        if (myEntityComponent.GetType().Name.Equals("NanoBotSuppressor"))
                        {
                            VoxelsPatch.Protectors = (HashSet<IMyUpgradeModule>)myEntityComponent.GetType()
                                .GetField("Protectors").GetValue(null);
                        }
                    }
                }
            }
            if (block.FatBlock is MyThrust)
            {
                if (__instance.ShipSoundComponent != null)
                    __instance.ShipSoundComponent.ShipHasChanged = true;

                m_cubeGrid.Components.Get<MyEntityThrustComponent>()?.MarkDirty();
            }

            if (__instance.ConveyorSystem != null && block.FatBlock is IMyConveyorEndpointBlock)
            {
                __instance.ConveyorSystem.UpdateLines();
                try
                {
                    if (SentisOptimisationsPlugin.Config.ConveyorCacheEnabled)
                    {
                        ConveyourCache.Remove(m_cubeGrid.EntityId);
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e);
                }
            }

            return false;
        }

        private static void MethodFlagForRecomputationPatched(MyGridConveyorSystem __instance)
        {
            try
            {
                if (!SentisOptimisationsPlugin.Config.ConveyorCacheEnabled)
                {
                    return;
                }
                MyCubeGrid Grid = (MyCubeGrid) ReflectionUtils.GetInstanceField(typeof(MyUpdateableGridSystem),
                    __instance, "<Grid>k__BackingField");
                ConveyourCache.Remove(Grid.EntityId);
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private static bool MethodOnBlockRemovedPatched(MyCubeGridSystems __instance, MySlimBlock block)
        {
            MyCubeGrid m_cubeGrid =
                (MyCubeGrid) ReflectionUtils.GetInstanceField(__instance.GetType(), __instance, "m_cubeGrid");
            if (block.FatBlock is MyThrust)
            {
                if (__instance.ShipSoundComponent != null)
                    __instance.ShipSoundComponent.ShipHasChanged = true;

                m_cubeGrid.Components.Get<MyEntityThrustComponent>()?.MarkDirty();
            }

            if (__instance.ConveyorSystem != null && block.FatBlock is IMyConveyorEndpointBlock)
            {
                __instance.ConveyorSystem.UpdateLines();
                try
                {
                    if (SentisOptimisationsPlugin.Config.ConveyorCacheEnabled)
                    {
                        ConveyourCache.Remove(m_cubeGrid.EntityId);
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e);
                }
            }

            return false;
        }

        public struct Key
        {
            public readonly long Part1;
            public readonly long Part2;
            public readonly MyDefinitionId? DefinitionId;

            public Key(long p1, long p2, MyDefinitionId? definitionId = null)
            {
                Part1 = p1;
                Part2 = p2;
                DefinitionId = definitionId;
            }

            public override bool Equals(object obj)
            {
                if (obj is Key)
                {
                    return Part1.Equals(((Key) obj).Part1) && Part2.Equals(((Key) obj).Part2) &&
                           Equals(DefinitionId, ((Key) obj).DefinitionId);
                }

                return false;
            }

            public override int GetHashCode()
            {
                if (DefinitionId == null)
                {
                    return Part1.GetHashCode() + Part2.GetHashCode();
                }

                return Part1.GetHashCode() + Part2.GetHashCode() + DefinitionId.GetHashCode();
            }
        }

        public static void OnUnloading()
        {
            CancellationTokenSource.Cancel();
        }

        public static void OnLoaded()
        {
            CancellationTokenSource = new CancellationTokenSource();
            ClearCacheLoop();
        }

        public static async void ClearCacheLoop()
        {
            try
            {
                Log.Info("ClearCache Loop started");
                while (!CancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(3600000);
                        await Task.Run(ClearCache);
                    }
                    catch (Exception e)
                    {
                        Log.Error("ClearCache Loop Error", e);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("ClearCache Loop start Error", e);
            }
        }

        private static void ClearCache()
        {
            ConveyourCache.Clear();
        }
    }
}