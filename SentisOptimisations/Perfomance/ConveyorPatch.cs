using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRage.Game;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class ConveyorPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static ConcurrentDictionary<Key, bool> conveyourCache = new ConcurrentDictionary<Key, bool>();

        public static void Patch(PatchContext ctx)
        {
            var MethodOnBlockAdded = typeof(MyCubeGridSystems).GetMethod
                (nameof(MyCubeGridSystems.OnBlockAdded), BindingFlags.Instance | BindingFlags.Public);


            ctx.GetPattern(MethodOnBlockAdded).Prefixes.Add(
                typeof(ConveyorPatch).GetMethod(nameof(MethodOnBlockAddedPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodOnBlockRemoved = typeof(MyCubeGridSystems).GetMethod
                (nameof(MyCubeGridSystems.OnBlockRemoved), BindingFlags.Instance | BindingFlags.Public);


            // Кэш конвеера
            ctx.GetPattern(MethodOnBlockRemoved).Prefixes.Add(
                typeof(ConveyorPatch).GetMethod(nameof(MethodOnBlockRemovedPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

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
                if (start is MyCubeBlock && end is MyCubeBlock)
                {
                    var startEntityId = ((MyCubeBlock) start).EntityId;
                    var endEntityId = ((MyCubeBlock) end).EntityId;
                    var cubeGridEntityId = ((MyCubeBlock) end).CubeGrid.EntityId;
                    var key = new Key(startEntityId, endEntityId, cubeGridEntityId, itemId);
                    if (conveyourCache.TryGetValue(key, out __result))
                    {
                        return false;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }

            return true;
        }

        private static void ComputeCanTransferPatchedPost(IMyConveyorEndpointBlock start,
            IMyConveyorEndpointBlock end, bool __result, MyDefinitionId? itemId)
        {
            try
            {
                if (start is MyCubeBlock && end is MyCubeBlock)
                {
                    var startEntityId = ((MyCubeBlock) start).EntityId;
                    var endEntityId = ((MyCubeBlock) end).EntityId;
                    var cubeGridEntityId = ((MyCubeBlock) end).CubeGrid.EntityId;
                    var key = new Key(startEntityId, endEntityId, cubeGridEntityId, itemId);
                    conveyourCache[key] = __result;
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
                    List<Key> toDel = new List<Key>();
                    foreach (var conveyourCacheKey in conveyourCache.Keys)
                    {
                        if (conveyourCacheKey.CubeGridId == m_cubeGrid.EntityId)
                        {
                            toDel.Add(conveyourCacheKey);
                        }
                    }

                    foreach (var key in toDel)
                    {
                        conveyourCache.Remove(key);
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
                MyCubeGrid Grid = (MyCubeGrid) ReflectionUtils.GetInstanceField(typeof(MyUpdateableGridSystem),
                    __instance, "<Grid>k__BackingField");
                List<Key> toDel = new List<Key>();
                foreach (var conveyourCacheKey in conveyourCache.Keys)
                {
                    if (conveyourCacheKey.CubeGridId == Grid.EntityId)
                    {
                        toDel.Add(conveyourCacheKey);
                    }
                }

                foreach (var key in toDel)
                {
                    conveyourCache.Remove(key);
                }
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
                    List<Key> toDel = new List<Key>();
                    foreach (var conveyourCacheKey in conveyourCache.Keys)
                    {
                        if (conveyourCacheKey.CubeGridId == m_cubeGrid.EntityId)
                        {
                            toDel.Add(conveyourCacheKey);
                        }
                    }

                    foreach (var key in toDel)
                    {
                        conveyourCache.Remove(key);
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
            public readonly long CubeGridId;
            public readonly MyDefinitionId? DefinitionId;

            public Key(long p1, long p2, long cubeGridId, MyDefinitionId? definitionId = null)
            {
                Part1 = p1;
                Part2 = p2;
                CubeGridId = cubeGridId;
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
    }
}