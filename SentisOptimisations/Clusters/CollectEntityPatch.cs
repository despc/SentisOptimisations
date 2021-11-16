using System.Collections.Generic;
using System.Reflection;
using Sandbox.Game.Entities;
using SentisOptimisations;
using SentisOptimisationsPlugin.Clusters;
using Torch.Managers.PatchManager;
using VRage.Collections;
using VRage.Game.Entity;

namespace FixTurrets.Clusters
{
    [PatchShim]
    public class CollectEntityPatch
    {
        public static void Patch(PatchContext ctx)
        {
            var AddEntityInternal = typeof(MyParallelEntityUpdateOrchestrator).GetMethod
                ("AddEntityInternal", BindingFlags.Instance | BindingFlags.NonPublic);
            var RemoveEntityInternal = typeof(MyParallelEntityUpdateOrchestrator).GetMethod
                ("RemoveWithFlags", BindingFlags.Instance | BindingFlags.NonPublic);
            ctx.GetPattern(AddEntityInternal).Prefixes.Add(
                typeof(CollectEntityPatch).GetMethod(nameof(AddEntityInternalPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            ctx.GetPattern(RemoveEntityInternal).Suffixes.Add(
                typeof(CollectEntityPatch).GetMethod(nameof(RemoveWithFlagsPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool AddEntityInternalPatched(MyEntity entity, MyParallelEntityUpdateOrchestrator __instance)
        {
            MyParallelUpdateFlags parallelUpdateFlags1;
            if (entity is IMyParallelUpdateable parallelUpdateable)
            {
                parallelUpdateFlags1 = parallelUpdateable.UpdateFlags;
            }
            else
            {
                parallelUpdateFlags1 = entity.NeedsUpdate.GetParallel();
                parallelUpdateable = (IMyParallelUpdateable) null;
            }

            MyParallelUpdateFlags parallelUpdateFlags2 = parallelUpdateFlags1;
            MyParallelUpdateFlags parallelUpdateFlags3;
            Dictionary<MyEntity, MyParallelUpdateFlags> m_lastUpdateRecord =
                (Dictionary<MyEntity, MyParallelUpdateFlags>) ReflectionUtils.GetInstanceField(__instance,
                    "m_lastUpdateRecord");
            if (m_lastUpdateRecord.TryGetValue(entity, out parallelUpdateFlags3))
            {
                parallelUpdateFlags2 = parallelUpdateFlags1 & ~parallelUpdateFlags3;
                MyParallelUpdateFlags flags = parallelUpdateFlags3 & ~parallelUpdateFlags1;
                ReflectionUtils.InvokeInstanceMethod(typeof(MyParallelEntityUpdateOrchestrator), __instance, "RemoveWithFlags", new object[] {entity, flags});
                //__instance.RemoveWithFlags(entity, flags);
            }

            if (parallelUpdateFlags2 != MyParallelUpdateFlags.NONE)
            {
                if ((parallelUpdateFlags2 & MyParallelUpdateFlags.BEFORE_NEXT_FRAME) != MyParallelUpdateFlags.NONE)
                    ((List<MyEntity>) ReflectionUtils.GetInstanceField(__instance,
                        "m_entitiesForUpdateOnce")).Add(entity);
                if ((parallelUpdateFlags2 & MyParallelUpdateFlags.EACH_FRAME_PARALLEL) != MyParallelUpdateFlags.NONE)
                    ReflectionUtils.InvokeInstanceMethod(__instance, "AddOnce", new object[]
                    {
                        ReflectionUtils.GetInstanceField(__instance,
                            "m_entitiesForUpdateParallel"),
                        parallelUpdateable
                    }, typeof(IMyParallelUpdateable));
                if ((parallelUpdateFlags2 & MyParallelUpdateFlags.EACH_FRAME) != MyParallelUpdateFlags.NONE)
                {
                    ReflectionUtils.InvokeInstanceMethod(__instance, "AddOnce", new object[]
                    {
                        ReflectionUtils.GetInstanceField(__instance,
                            "m_entitiesForUpdate"),
                        entity
                    }, typeof(MyEntity));
                    if (!ClusterBuilder.IsForSerialUpdate(entity))
                    {
                        ClusterBuilder.m_entitiesForUpdate.Add(entity);
                        ClusterBuilder.m_entitiesForUpdateId.Add(entity.EntityId);
                    }
                }

                if ((parallelUpdateFlags2 & MyParallelUpdateFlags.EACH_10TH_FRAME) != MyParallelUpdateFlags.NONE)
                {
                    ((MyDistributedUpdater<List<MyEntity>, MyEntity>) ReflectionUtils.GetInstanceField(__instance,
                        "m_entitiesForUpdate10")).List.Add(entity);
                    if (!ClusterBuilder.IsForSerialUpdate(entity))
                    {
                        ClusterBuilder.m_entitiesForUpdate10.List.Add(entity);
                        ClusterBuilder.m_entitiesForUpdate10Id.Add(entity.EntityId);
                    }
                }

                if ((parallelUpdateFlags2 & MyParallelUpdateFlags.EACH_100TH_FRAME) != MyParallelUpdateFlags.NONE)
                {
                    ((MyDistributedUpdater<List<MyEntity>, MyEntity>) ReflectionUtils.GetInstanceField(__instance,
                        "m_entitiesForUpdate100")).List.Add(entity);
                    if (!ClusterBuilder.IsForSerialUpdate(entity))
                    {
                        ClusterBuilder.m_entitiesForUpdate100.List.Add(entity);
                        ClusterBuilder.m_entitiesForUpdate100Id.Add(entity.EntityId);
                    }
                }

                if ((parallelUpdateFlags2 & MyParallelUpdateFlags.SIMULATE) != MyParallelUpdateFlags.NONE)
                {
                    ((List<MyEntity>) ReflectionUtils.GetInstanceField(__instance,
                        "m_entitiesForSimulate")).Add(entity);
                }
            }

            m_lastUpdateRecord[entity] = parallelUpdateFlags1;
            return false;
        }

        private static void RemoveWithFlagsPatched(MyEntity entity, MyParallelUpdateFlags flags)
        {
            if ((flags & MyParallelUpdateFlags.EACH_FRAME) != MyParallelUpdateFlags.NONE)
            {
                ClusterBuilder.m_entitiesForUpdate.Remove(entity);
                ClusterBuilder.m_entitiesForUpdateId.Remove(entity.EntityId);
            }

            if ((flags & MyParallelUpdateFlags.EACH_10TH_FRAME) != MyParallelUpdateFlags.NONE)
            {
                ClusterBuilder.m_entitiesForUpdate10.List.Remove(entity);
                ClusterBuilder.m_entitiesForUpdate10Id.Remove(entity.EntityId);
            }

            if ((flags & MyParallelUpdateFlags.EACH_100TH_FRAME) != MyParallelUpdateFlags.NONE)
            {
                ClusterBuilder.m_entitiesForUpdate100.List.Remove(entity);
                ClusterBuilder.m_entitiesForUpdate100Id.Remove(entity.EntityId);
            }
        }
    }
}