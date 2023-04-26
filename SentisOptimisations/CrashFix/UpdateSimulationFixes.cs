using NLog;
using Sandbox.Game.Entities;
using System;
using System.Collections.Generic;
using System.Reflection;
using NAPI;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRage.Collections;
using VRage.Game.Entity;
using VRage.ModAPI;


namespace SentisOptimisationsPlugin
{
    // Одолжил у Dori
    public static class UpdateSimulationFixes
    {
        private static FieldInfo m_entitiesForUpdate100;
        private static FieldInfo m_entitiesForUpdate10;
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            m_entitiesForUpdate10 = typeof(MyParallelEntityUpdateOrchestrator).EasyField("m_entitiesForUpdate10");
            m_entitiesForUpdate100 = typeof(MyParallelEntityUpdateOrchestrator).EasyField("m_entitiesForUpdate100");

            ctx.Prefix(typeof(MyParallelEntityUpdateOrchestrator), typeof(UpdateSimulationFixes),
                nameof(UpdateAfterSimulation10));

            ctx.Prefix(typeof(MyParallelEntityUpdateOrchestrator), typeof(UpdateSimulationFixes),
                nameof(UpdateAfterSimulation100));

            ctx.Prefix(typeof(MyParallelEntityUpdateOrchestrator), typeof(UpdateSimulationFixes),
                nameof(UpdateBeforeSimulation100));
            ctx.Prefix(typeof(MyParallelEntityUpdateOrchestrator), typeof(UpdateSimulationFixes),
                nameof(ParallelUpdateHandlerAfterSimulation));
        }

        private static bool UpdateAfterSimulation10(MyParallelEntityUpdateOrchestrator __instance)
        {
            if (__instance == null)
                return false;

            try
            {
                var My_entitiesForUpdate10 =
                    (MyDistributedUpdater<List<MyEntity>, MyEntity>)m_entitiesForUpdate10.GetValue(__instance);
                if (My_entitiesForUpdate10 == null)
                    return false;

                foreach (MyEntity myEntity in My_entitiesForUpdate10)
                {
                    if (myEntity != null && !myEntity.MarkedForClose &&
                        (myEntity.Flags & EntityFlags.NeedsUpdate10) != (EntityFlags)0 && myEntity.InScene)
                        myEntity.UpdateAfterSimulation10();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during UpdateAfterSimulation10 entity update! Crash Avoided");
            }

            return false;
        }

        private static bool UpdateAfterSimulation100(MyParallelEntityUpdateOrchestrator __instance)
        {
            if (__instance == null)
                return false;

            try
            {
                var My_entitiesForUpdate100 =
                    (MyDistributedUpdater<List<MyEntity>, MyEntity>)m_entitiesForUpdate100.GetValue(__instance);
                if (My_entitiesForUpdate100 == null)
                    return false;

                foreach (MyEntity myEntity in My_entitiesForUpdate100)
                {
                    if (myEntity != null && !myEntity.MarkedForClose &&
                        (myEntity.Flags & EntityFlags.NeedsUpdate100) != (EntityFlags)0 && myEntity.InScene)
                        myEntity.UpdateAfterSimulation100();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during UpdateAfterSimulation100 entity update! Crash Avoided");
            }

            return false;
        }

        private static bool UpdateBeforeSimulation100(MyParallelEntityUpdateOrchestrator __instance)
        {
            if (__instance == null)
                return false;

            try
            {
                var My_entitiesForUpdate100 =
                    (MyDistributedUpdater<List<MyEntity>, MyEntity>)m_entitiesForUpdate100.GetValue(__instance);
                // checking for Null here saving us from crash,
                if (My_entitiesForUpdate100 == null)
                    return false;

                My_entitiesForUpdate100.Update();

                foreach (MyEntity myEntity in My_entitiesForUpdate100)
                {
                    if (myEntity != null && !myEntity.MarkedForClose &&
                        (myEntity.Flags & EntityFlags.NeedsUpdate100) != (EntityFlags)0 && myEntity.InScene)
                        myEntity.UpdateBeforeSimulation100();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during UpdateBeforeSimulation100 entity update! Crash Avoided");
            }

            return false;
        }

        private static bool ParallelUpdateHandlerAfterSimulation(IMyParallelUpdateable entity)
        {
            if (entity != null && !entity.MarkedForClose &&
                (entity.UpdateFlags & MyParallelUpdateFlags.EACH_FRAME_PARALLEL) != MyParallelUpdateFlags.NONE &&
                entity.InScene)
                entity.UpdateAfterSimulationParallel();

            return false;
        }
    }
}