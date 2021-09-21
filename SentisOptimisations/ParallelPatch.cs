using System;
using System.Collections.Generic;
using System.Net.Configuration;
using System.Reflection;
using Havok;
using NLog;
using ParallelTasks;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Debris;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Weapons;
using Sandbox.ModAPI;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Collections;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class ParallelPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static readonly Random r = new Random();
        private static Action<KeyValuePair<long, HashSet<MyEntity>>> m_parallelUpdateHandlerAfterSimulation;
        private static Action<KeyValuePair<long, HashSet<MyEntity>>> m_parallelUpdateHandlerAfterSimulation10;
        private static Action<KeyValuePair<long, HashSet<MyEntity>>> m_parallelUpdateHandlerBeforeSimulation;
        
        public static bool Enabled = true;

        public static void Patch(PatchContext ctx)
        {
            // var UpdateBeforeSimulation = typeof(MyParallelEntityUpdateOrchestrator).GetMethod
            //     ("UpdateBeforeSimulation", BindingFlags.Instance | BindingFlags.NonPublic);
            //
            // ctx.GetPattern(UpdateBeforeSimulation).Prefixes.Add(
            //     typeof(ParallelPatch).GetMethod(nameof(UpdateBeforeSimulationPatched),
            //         BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var UpdateAfterSimulation = typeof(MyParallelEntityUpdateOrchestrator).GetMethod
                ("UpdateAfterSimulation", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(UpdateAfterSimulation).Prefixes.Add(
                typeof(ParallelPatch).GetMethod(nameof(UpdateAfterSimulationPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            // var UpdateAfterSimulation10 = typeof(MyParallelEntityUpdateOrchestrator).GetMethod
            //     ("UpdateAfterSimulation10", BindingFlags.Instance | BindingFlags.NonPublic);
            //
            // ctx.GetPattern(UpdateAfterSimulation10).Prefixes.Add(
            //     typeof(ParallelPatch).GetMethod(nameof(UpdateAfterSimulationPatched10),
            //         BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var UpdateExplosions = typeof(MyGridPhysics).GetMethod
                ("UpdateExplosions", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(UpdateExplosions).Prefixes.Add(
                typeof(ParallelPatch).GetMethod(nameof(UpdateExplosionsPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            
            var MethodAddEntityWithId = typeof(MyEntityIdentifier).GetMethod
                (nameof(MyEntityIdentifier.AddEntityWithId), BindingFlags.Static | BindingFlags.Public);
           
            ctx.GetPattern(MethodAddEntityWithId).Prefixes.Add(
                typeof(ParallelPatch).GetMethod(nameof(MethodAddEntityWithIdPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            m_parallelUpdateHandlerAfterSimulation =
                new Action<KeyValuePair<long, HashSet<MyEntity>>>(ParallelUpdateHandlerAfterSimulation);
            m_parallelUpdateHandlerAfterSimulation10 =
                new Action<KeyValuePair<long, HashSet<MyEntity>>>(ParallelUpdateHandlerAfterSimulation10);
            m_parallelUpdateHandlerBeforeSimulation =
                new Action<KeyValuePair<long, HashSet<MyEntity>>>(ParallelUpdateHandlerBeforeSimulation);
        }

        private static bool UpdateExplosionsPatched(MyGridPhysics __instance)
        {
            try
            {
                List<MyGridPhysics.ExplosionInfo> m_explosions =
                    (List<MyGridPhysics.ExplosionInfo>) GetInstanceField(typeof(MyGridPhysics), __instance,
                        "m_explosions");
                MyCubeGrid m_grid = (MyCubeGrid) GetInstanceField(typeof(MyGridPhysics), __instance, "m_grid");
                ReflectionUtils.SetInstanceField(typeof(MyGridPhysics), __instance, "m_debrisPerFrame",
                    0);
                //this.m_debrisPerFrame = 0;
                if (m_explosions.Count <= 0)
                    return false;
                if (Sync.IsServer)
                {
                    ReflectionUtils.InvokeInstanceMethod(typeof(MyCubeGrid), m_grid, "PerformCutouts",
                        new[] {m_explosions});
                    //m_grid.PerformCutouts(m_explosions);
                    float initialSpeed = m_grid.Physics.LinearVelocity.Length();
                    foreach (MyGridPhysics.ExplosionInfo explosion in m_explosions)
                    {
                        int m_debrisPerFrame =
                            (int) GetInstanceField(typeof(MyGridPhysics), __instance, "m_debrisPerFrame");
                        if ((double) initialSpeed > 0.0 && explosion.GenerateDebris && m_debrisPerFrame < 3)
                        {
                            MyDebris.Static.CreateDirectedDebris((Vector3) explosion.Position,
                                m_grid.Physics.LinearVelocity / initialSpeed, m_grid.GridSize, m_grid.GridSize * 1.5f,
                                0.0f, 1.570796f, 6, initialSpeed);
                            ReflectionUtils.SetInstanceField(typeof(MyGridPhysics), __instance, "m_debrisPerFrame",
                                m_debrisPerFrame + 1);
                        }
                    }
                }

                m_explosions.Clear();
            }
            catch (Exception e)
            {
                Log.Error(e);
            }

            return false;
        }

        private static bool MethodAddEntityWithIdPatched(ref IMyEntity entity)
        {
            BindingFlags bindFlags = BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo field = typeof(MyEntityIdentifier).GetField("m_mainData", bindFlags);
            Object d = field.GetValue(null);
            Dictionary<long, IMyEntity> EntityList = (Dictionary<long, IMyEntity>) GetInstanceField(d.GetType(), d, "EntityList");
            var entityEntityId = entity.EntityId;
            while (EntityList.ContainsKey(entityEntityId))
            {
                entityEntityId = entityEntityId + 777;
                entity.EntityId = entityEntityId;
            }
            return true;
        }
        

        private static void ParallelUpdateHandlerAfterSimulation(KeyValuePair<long, HashSet<MyEntity>> pair)
        {
            HashSet<MyEntity> entities = pair.Value;
            foreach (var myEntity in entities)
            {
                myEntity.UpdateAfterSimulation();
            }
        }
        
        private static void ParallelUpdateHandlerAfterSimulation10(KeyValuePair<long, HashSet<MyEntity>> pair)
        {
            HashSet<MyEntity> entities = pair.Value;
            foreach (var myEntity in entities)
            {
                myEntity.UpdateAfterSimulation10();
            }
        }

        private static void ParallelUpdateHandlerBeforeSimulation(KeyValuePair<long, HashSet<MyEntity>> pair)
        {
            HashSet<MyEntity> entities = pair.Value;
            foreach (var myEntity in entities)
            {
                myEntity.UpdateBeforeSimulation();
            }
        }


        private static bool UpdateAfterSimulationPatched(MyParallelEntityUpdateOrchestrator __instance)
        {
            try
            {
                if (!Enabled)
                {
                    return true;
                }

                HashSet<MyEntity> forParallelUpdate = new HashSet<MyEntity>();
                HashSet<MyEntity> m_entitiesForUpdate =
                    (HashSet<MyEntity>) GetInstanceField(typeof(MyParallelEntityUpdateOrchestrator), __instance,
                        "m_entitiesForUpdate");
                foreach (MyEntity myEntity in m_entitiesForUpdate)
                {
                    if (!myEntity.MarkedForClose && (myEntity.Flags & EntityFlags.NeedsUpdate) != (EntityFlags) 0 &&
                        myEntity.InScene)
                    {
                        if (!(myEntity is MyLargeTurretBase))
                        {
                            myEntity.UpdateAfterSimulation();
                        }

                    }
                }
                UpdateAfterSimulationInThread(forParallelUpdate);
            }
            catch (Exception e)
            {
                Log.Error("Exception in ParallelPatch", e);
            }

            return false;
        }
 
        public static void UpdateBeforeSimulationInThread(Dictionary<long, HashSet<MyEntity>> packForSingleThread)
        {
            using (HkAccessControl.PushState(HkAccessControl.AccessState.SharedRead))
            {
                using (MyEntities.StartAsyncUpdateBlock())
                    Parallel.ForEach(packForSingleThread, m_parallelUpdateHandlerBeforeSimulation,
                        MyParallelEntityUpdateOrchestrator.WorkerPriority, blocking: true);
            }
        }

         public static void UpdateAfterSimulationInThread(HashSet<MyEntity> forParallelUpdate)
        {
            using (HkAccessControl.PushState(HkAccessControl.AccessState.SharedRead))
            {
                using (MyEntities.StartAsyncUpdateBlock())
                    
                    Parallel.ForEach(forParallelUpdate, entity => entity.UpdateAfterSimulation(),
                        WorkPriority.VeryHigh, blocking: true);
            }
        }
        
        public static void UpdateAfterSimulationInThread10(Dictionary<long, HashSet<MyEntity>> packForSingleThread)
        {
            using (HkAccessControl.PushState(HkAccessControl.AccessState.SharedRead))
            {
                using (MyEntities.StartAsyncUpdateBlock())
                    
                    Parallel.ForEach(packForSingleThread, m_parallelUpdateHandlerAfterSimulation10,
                        WorkPriority.VeryHigh, blocking: true);
            }
        }

        internal static object GetInstanceField(Type type, object instance, string fieldName)
        {
            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Static;
            FieldInfo field = type.GetField(fieldName, bindFlags);
            return field.GetValue(instance);
        }
    }
}