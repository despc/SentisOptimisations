using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Havok;
using NLog;
using ParallelTasks;
using Sandbox;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Debris;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.RenderDirect.ActorComponents;
using SentisOptimisations;
using SentisOptimisationsPlugin.Clusters;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Collections;
using VRage.Game.Entity;
using VRage.Library.Utils;
using VRage.ModAPI;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class ParallelPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static readonly Random r = new Random();
        private static Action<KeyValuePair<long, List<MyEntity>>> m_parallelUpdateHandlerAfterSimulation;
        private static Action<KeyValuePair<long, List<MyEntity>>> m_parallelUpdateHandlerAfterSimulation10;
        private static Action<KeyValuePair<long, List<MyEntity>>> m_parallelUpdateHandlerAfterSimulation100;
        private static Action<KeyValuePair<long, List<MyEntity>>> m_parallelUpdateHandlerBeforeSimulation;

        private static readonly object AllocateIdLock = new object();
        private static readonly object DisconnectsLock = new object();
        private static readonly object SplitLock = new object();
        private static readonly object MyEntitiesUpdateBeforeSimulationLock = new object();
        private static readonly object AddGravityLock = new object();
        private static readonly object ThrustDamageLock = new object();
        private static bool DelayFlag = false;
        public static bool Enabled = false;

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
            //
            var UpdateAfterSimulation10 = typeof(MyParallelEntityUpdateOrchestrator).GetMethod
                ("UpdateAfterSimulation10", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(UpdateAfterSimulation10).Prefixes.Add(
                typeof(ParallelPatch).GetMethod(nameof(UpdateAfterSimulation10Patched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var UpdateAfterSimulation100 = typeof(MyParallelEntityUpdateOrchestrator).GetMethod
                ("UpdateAfterSimulation100", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(UpdateAfterSimulation100).Prefixes.Add(
                typeof(ParallelPatch).GetMethod(nameof(UpdateAfterSimulation100Patched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var UpdateExplosions = typeof(MyGridPhysics).GetMethod
                ("UpdateExplosions", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(UpdateExplosions).Prefixes.Add(
                typeof(ParallelPatch).GetMethod(nameof(UpdateExplosionsPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var ThrustDamageAsync = typeof(MyThrust).GetMethod
                ("ThrustDamageAsync", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(ThrustDamageAsync).Prefixes.Add(
                typeof(ParallelPatch).GetMethod(nameof(ThrustDamageAsyncPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodAllocateId = typeof(MyEntityIdentifier).GetMethod
                (nameof(MyEntityIdentifier.AllocateId), BindingFlags.Static | BindingFlags.Public);

            ctx.GetPattern(MethodAllocateId).Prefixes.Add(
                typeof(ParallelPatch).GetMethod(nameof(AllocateIdPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodCreateGridForSplit = typeof(MyCubeGrid).GetMethod
                ("CreateGridForSplit", BindingFlags.Static | BindingFlags.NonPublic);

            ctx.GetPattern(MethodCreateGridForSplit).Prefixes.Add(
                typeof(ParallelPatch).GetMethod(nameof(CreateGridForSplitPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodDoDisconnects = typeof(MyDisconnectHelper).GetMethod
                ("DoDisconnects", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodDoDisconnects).Prefixes.Add(
                typeof(ParallelPatch).GetMethod(nameof(DoDisconnectsPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodUpdateBeforeSimulationParallel = typeof(MyCubeGrid).GetMethod
                ("UpdateBeforeSimulationParallel", BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodUpdateBeforeSimulationParallel).Prefixes.Add(
                typeof(ParallelPatch).GetMethod(nameof(UpdateBeforeSimulationParallel),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodUpdateBeforeSimulation = typeof(MyCubeGrid).GetMethod
                ("UpdateBeforeSimulation", BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodUpdateBeforeSimulation).Prefixes.Add(
                typeof(ParallelPatch).GetMethod(nameof(UpdateBeforeSimulationFixPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            m_parallelUpdateHandlerAfterSimulation =
                new Action<KeyValuePair<long, List<MyEntity>>>(ParallelUpdateHandlerAfterSimulation);
            m_parallelUpdateHandlerAfterSimulation10 =
                new Action<KeyValuePair<long, List<MyEntity>>>(ParallelUpdateHandlerAfterSimulation10);
            m_parallelUpdateHandlerAfterSimulation100 =
                new Action<KeyValuePair<long, List<MyEntity>>>(ParallelUpdateHandlerAfterSimulation100);
            m_parallelUpdateHandlerBeforeSimulation =
                new Action<KeyValuePair<long, List<MyEntity>>>(ParallelUpdateHandlerBeforeSimulation);
        }

        private static bool ThrustDamageAsyncPatched(MyThrust __instance, uint dmgTimeMultiplier)
        {
            try
            {
                if (r.NextDouble() > 0.4)
                {
                    return false;
                }
                ListReader<MyThrustFlameAnimator.FlameInfo> m_flames =
                    (ListReader<MyThrustFlameAnimator.FlameInfo>) ReflectionUtils.GetInstanceField(typeof(MyThrust),
                        __instance, "m_flames");
                if (m_flames.Count <= 0 || !MySession.Static.ThrusterDamage ||
                    (!__instance.IsWorking || !__instance.CubeGrid.InScene)
                    || (__instance.CubeGrid.Physics == null || !__instance.CubeGrid.Physics.Enabled ||
                        !Sync.IsServer)
                    || ((double) __instance.CurrentStrength == 0.0 && !MyFakes.INACTIVE_THRUSTER_DMG ||
                        !MyFakes.INACTIVE_THRUSTER_DMG))
                    return false;
                foreach (MyThrustFlameAnimator.FlameInfo flame in m_flames)
                {
                    MatrixD worldMatrix = __instance.WorldMatrix;
                    List<HkBodyCollision> m_flameCollisionsList =
                        (List<HkBodyCollision>) ReflectionUtils.GetPrivateStaticField(typeof(MyThrust),
                            "m_flameCollisionsList");
                    var damageCapsuleLine = __instance.GetDamageCapsuleLine(flame, ref worldMatrix);
                    ReflectionUtils.InvokeInstanceMethod(typeof(MyThrust), __instance, "ThrustDamageShapeCast",
                        new object[] {damageCapsuleLine, flame, m_flameCollisionsList});
                    lock (ThrustDamageLock)
                    {
                        ReflectionUtils.InvokeInstanceMethod(typeof(MyThrust), __instance, "ThrustDamageDealDamage",
                            new object[] {flame, m_flameCollisionsList, dmgTimeMultiplier});
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }

            return false;
        }

        private static bool UpdateExplosionsPatched(MyGridPhysics __instance)
        {
            lock (SplitLock)
            {
                try
                {
                    List<MyGridPhysics.ExplosionInfo> m_explosions =
                        (List<MyGridPhysics.ExplosionInfo>) ReflectionUtils.GetInstanceField(typeof(MyGridPhysics),
                            __instance,
                            "m_explosions");
                    MyCubeGrid m_grid =
                        (MyCubeGrid) ReflectionUtils.GetInstanceField(typeof(MyGridPhysics), __instance, "m_grid");
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
                                (int) ReflectionUtils.GetInstanceField(typeof(MyGridPhysics), __instance,
                                    "m_debrisPerFrame");
                            if ((double) initialSpeed > 0.0 && explosion.GenerateDebris && m_debrisPerFrame < 3)
                            {
                                MyDebris.Static.CreateDirectedDebris((Vector3) explosion.Position,
                                    m_grid.Physics.LinearVelocity / initialSpeed, m_grid.GridSize,
                                    m_grid.GridSize * 1.5f,
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
            }

            return false;
        }

        private static bool UpdateBeforeSimulationParallel(MyCubeGrid __instance)
        {
            try
            {
                ReflectionUtils.InvokeInstanceMethod(typeof(MyCubeGrid), __instance, "DispatchOnce",
                    new object[] {MyCubeGrid.UpdateQueue.OnceBeforeSimulation, true});
                ReflectionUtils.InvokeInstanceMethod(typeof(MyCubeGrid), __instance, "Dispatch",
                    new object[] {MyCubeGrid.UpdateQueue.BeforeSimulation, true});
            }
            catch (Exception e)
            {
                Log.Error(e);
            }

            return false;
        }

        private static bool UpdateBeforeSimulationFixPatched(MyCubeGrid __instance)
        {
            try
            {
                MySimpleProfiler.Begin("Grid", MySimpleProfiler.ProfilingBlockType.BLOCK,
                    nameof(MyCubeGrid.UpdateBeforeSimulation));
                ReflectionUtils.InvokeInstanceMethod(typeof(MyCubeGrid), __instance, "DispatchOnce",
                    new object[] {MyCubeGrid.UpdateQueue.OnceBeforeSimulation, false});
                ReflectionUtils.InvokeInstanceMethod(typeof(MyCubeGrid), __instance, "Dispatch",
                    new object[] {MyCubeGrid.UpdateQueue.BeforeSimulation, false});
                MySimpleProfiler.End(nameof(MyCubeGrid.UpdateBeforeSimulation));
            }
            catch (Exception e)
            {
                Log.Error(e);
            }

            return false;
        }

        private static bool DoDisconnectsPatched(MyDisconnectHelper __instance, MyCubeGrid grid,
            MyCubeGrid.MyTestDisconnectsReason reason)
        {
            lock (DisconnectsLock)
            {
                try
                {
                    List<MySlimBlock> m_sortedBlocks =
                        (List<MySlimBlock>) ReflectionUtils.GetInstanceField(typeof(MyDisconnectHelper), __instance,
                            "m_sortedBlocks");
                    MyDisconnectHelper.Group m_largestGroupWithPhysics =
                        (MyDisconnectHelper.Group) ReflectionUtils.GetInstanceField(typeof(MyDisconnectHelper),
                            __instance, "m_largestGroupWithPhysics");
                    List<MyDisconnectHelper.Group> m_groups =
                        (List<MyDisconnectHelper.Group>) ReflectionUtils.GetInstanceField(typeof(MyDisconnectHelper),
                            __instance, "m_groups");

                    m_sortedBlocks.RemoveRange(m_largestGroupWithPhysics.FirstBlockIndex,
                        m_largestGroupWithPhysics.BlockCount);
                    for (int index = 0; index < m_groups.Count; ++index)
                    {
                        MyDisconnectHelper.Group group = m_groups[index];
                        if (group.FirstBlockIndex > m_largestGroupWithPhysics.FirstBlockIndex)
                        {
                            group.FirstBlockIndex -= m_largestGroupWithPhysics.BlockCount;
                            m_groups[index] = group;
                        }
                    }

                    MyCubeGrid.CreateSplits(grid, m_sortedBlocks, m_groups, reason);
                    m_groups.Clear();
                    m_sortedBlocks.Clear();
                    HashSet<MySlimBlock> m_disconnectHelper =
                        (HashSet<MySlimBlock>) ReflectionUtils.GetInstanceField(typeof(MyDisconnectHelper), __instance,
                            "m_disconnectHelper");
                    m_disconnectHelper.Clear();
                }
                catch (Exception e)
                {
                    //Log.Error("DoDisconnectsPatched Error");
                }
            }

            return false;
        }

        private static bool AllocateIdPatched(ref long __result,
            MyEntityIdentifier.ID_OBJECT_TYPE objectType = MyEntityIdentifier.ID_OBJECT_TYPE.ENTITY,
            MyEntityIdentifier.ID_ALLOCATION_METHOD generationMethod = MyEntityIdentifier.ID_ALLOCATION_METHOD.RANDOM)
        {
            lock (AllocateIdLock)
            {
                long[] m_lastGeneratedIds =
                    (long[]) ReflectionUtils.GetPrivateStaticField(typeof(MyEntityIdentifier), "m_lastGeneratedIds");
                long uniqueNumber = generationMethod != MyEntityIdentifier.ID_ALLOCATION_METHOD.RANDOM
                    ? Interlocked.Increment(ref m_lastGeneratedIds[(int) objectType])
                    : MyRandom.Instance.NextLong() & 72057594037927935L;
                __result = MyEntityIdentifier.ConstructId(objectType, uniqueNumber);
                return false;
            }
        }

        private static bool CreateGridForSplitPatched(ref long newEntityId)
        {
            AllocateIdPatched(ref newEntityId);
            return true;
        }


        private static void ParallelUpdateHandlerAfterSimulation(KeyValuePair<long, List<MyEntity>> pair)
        {
            List<MyEntity> entities = pair.Value;
            UpdateSetAfterSimulation(entities);
        }

        private static void UpdateSetAfterSimulation(List<MyEntity> entities)
        {
            foreach (var myEntity in entities)
            {
                if (myEntity != null && !myEntity.MarkedForClose &&
                    (myEntity.Flags & EntityFlags.NeedsUpdate) != (EntityFlags) 0 &&
                    myEntity.InScene)
                {
                    try
                    {
                        myEntity.UpdateAfterSimulation();
                    }
                    catch (Exception e)
                    {
                        Log.Error(e);
                    }
                }
            }
        }

        private static void ParallelUpdateHandlerAfterSimulation10(KeyValuePair<long, List<MyEntity>> pair)
        {
            List<MyEntity> entities = pair.Value;

            UpdateSetAfterSimulation10(entities);
        }

        private static void UpdateSetAfterSimulation10(List<MyEntity> entities)
        {
            foreach (var myEntity in entities)
            {
                if (myEntity == null)
                {
                    continue;
                }

                if ((ulong) myEntity.EntityId % 10 != MySandboxGame.Static.SimulationFrameCounter % 10)
                {
                    continue;
                }

                if (!myEntity.MarkedForClose && (myEntity.Flags & EntityFlags.NeedsUpdate10) != (EntityFlags) 0 &&
                    myEntity.InScene)
                {
                    try
                    {
                        myEntity.UpdateAfterSimulation10();
                    }
                    catch (Exception e)
                    {
                        Log.Error(e);
                    }
                }
            }
        }

        private static void ParallelUpdateHandlerAfterSimulation100(KeyValuePair<long, List<MyEntity>> pair)
        {
            List<MyEntity> entities = pair.Value;
            UpdateSetAfterSimulation100(entities);
        }

        private static void UpdateSetAfterSimulation100(List<MyEntity> entities)
        {
            foreach (var myEntity in entities)
            {
                if (myEntity == null)
                {
                    continue;
                }

                if ((ulong) myEntity.EntityId % 100 != MySandboxGame.Static.SimulationFrameCounter % 100)
                {
                    continue;
                }

                if (!myEntity.MarkedForClose && (myEntity.Flags & EntityFlags.NeedsUpdate100) != (EntityFlags) 0 &&
                    myEntity.InScene)
                {
                    try
                    {
                        myEntity.UpdateAfterSimulation100();
                    }
                    catch (Exception e)
                    {
                        Log.Error(e);
                    }
                }
            }
        }

        private static void ParallelUpdateHandlerBeforeSimulation(KeyValuePair<long, List<MyEntity>> pair)
        {
            List<MyEntity> entities = pair.Value;
            foreach (var myEntity in entities)
            {
                if (!myEntity.MarkedForClose && (myEntity.Flags & EntityFlags.NeedsUpdate) != (EntityFlags) 0 &&
                    myEntity.InScene)
                {
                    try
                    {
                        myEntity.UpdateBeforeSimulation();
                    }
                    catch (Exception e)
                    {
                        Log.Error(e);
                    }
                }
            }
        }

        private static bool UpdateAfterSimulationPatched()
        {
            if (!SentisOptimisationsPlugin.Config.ClustersEnabled)
            {
                return true;
            }
            try
            {
                if (!DelayFlag)
                {
                    var staticSimulationFrameCounter = MySandboxGame.Static.SimulationFrameCounter;
                    if (staticSimulationFrameCounter > 300)
                    {
                        DelayFlag = true;
                    }

                    return true;
                }

                UpdateAfterSimulationInThread(SentisOptimisationsPlugin._cb.Clusters);
                lock (ClusterBuilder.buildClustersLock)
                {
                    UpdateSetAfterSimulation(SentisOptimisationsPlugin._cb.ForSerialUpdate);
                }
            }
            catch (Exception e)
            {
                Log.Error("Exception in ParallelPatch", e);
            }

            return false;
        }

        private static bool UpdateAfterSimulation10Patched()
        {
            if (!SentisOptimisationsPlugin.Config.ClustersEnabled)
            {
                return true;
            }
            
            try
            {
                if (!DelayFlag)
                {
                    var staticSimulationFrameCounter = MySandboxGame.Static.SimulationFrameCounter;
                    if (staticSimulationFrameCounter > 300)
                    {
                        DelayFlag = true;
                    }

                    return true;
                }

                UpdateAfterSimulation10InThread(SentisOptimisationsPlugin._cb.Clusters10);
                lock (ClusterBuilder.buildClustersLock10)
                {
                    UpdateSetAfterSimulation10(SentisOptimisationsPlugin._cb.ForSerialUpdate10);
                }
            }
            catch (Exception e)
            {
                Log.Error("Exception in ParallelPatch", e);
            }

            return false;
        }

        private static bool UpdateAfterSimulation100Patched()
        {
            if (!SentisOptimisationsPlugin.Config.ClustersEnabled)
            {
                return true;
            }
            try
            {
                if (!DelayFlag)
                {
                    var staticSimulationFrameCounter = MySandboxGame.Static.SimulationFrameCounter;
                    if (staticSimulationFrameCounter > 300)
                    {
                        DelayFlag = true;
                    }

                    return true;
                }

                UpdateAfterSimulation100InThread(SentisOptimisationsPlugin._cb.Clusters100);
                lock (ClusterBuilder.buildClustersLock100)
                {
                    UpdateSetAfterSimulation100(SentisOptimisationsPlugin._cb.ForSerialUpdate100);
                }
            }
            catch (Exception e)
            {
                Log.Error("Exception in ParallelPatch", e);
            }

            return false;
        }

        private static bool UpdateBeforeSimulationPatched()
        {
            if (!SentisOptimisationsPlugin.Config.ClustersEnabled)
            {
                return true;
            }
            try
            {
                if (!DelayFlag)
                {
                    var staticSimulationFrameCounter = MySandboxGame.Static.SimulationFrameCounter;
                    if (staticSimulationFrameCounter > 300)
                    {
                        DelayFlag = true;
                    }

                    return true;
                }

                UpdateBeforeSimulationInThread(SentisOptimisationsPlugin._cb.Clusters);
            }
            catch (Exception e)
            {
                Log.Error("Exception in ParallelPatch", e);
            }

            return false;
        }

        public static void UpdateBeforeSimulationInThread(Dictionary<long, List<MyEntity>> clusters)
        {
            using (HkAccessControl.PushState(HkAccessControl.AccessState.SharedRead))
            {
                using (MyEntities.StartAsyncUpdateBlock())
                    lock (ClusterBuilder.buildClustersLock)
                    {
                        Parallel.ForEach(new Dictionary<long, List<MyEntity>>(clusters),
                            m_parallelUpdateHandlerBeforeSimulation, WorkPriority.VeryHigh, blocking: true);
                    }
            }
        }

        public static void UpdateAfterSimulationInThread(Dictionary<long, List<MyEntity>> clusters)
        {
            using (HkAccessControl.PushState(HkAccessControl.AccessState.SharedRead))
            {
                using (MyEntities.StartAsyncUpdateBlock())
                    lock (ClusterBuilder.buildClustersLock)
                    {
                        Parallel.ForEach(new Dictionary<long, List<MyEntity>>(clusters),
                            m_parallelUpdateHandlerAfterSimulation, WorkPriority.VeryHigh, blocking: true);
                    }
            }
        }

        public static void UpdateAfterSimulation10InThread(Dictionary<long, List<MyEntity>> clusters)
        {
            using (HkAccessControl.PushState(HkAccessControl.AccessState.SharedRead))
            {
                using (MyEntities.StartAsyncUpdateBlock())
                    lock (ClusterBuilder.buildClustersLock10)
                    {
                        Parallel.ForEach(new Dictionary<long, List<MyEntity>>(clusters),
                            m_parallelUpdateHandlerAfterSimulation10, WorkPriority.VeryHigh, blocking: true);
                    }
            }
        }

        public static void UpdateAfterSimulation100InThread(Dictionary<long, List<MyEntity>> clusters)
        {
            using (HkAccessControl.PushState(HkAccessControl.AccessState.SharedRead))
            {
                using (MyEntities.StartAsyncUpdateBlock())
                    lock (ClusterBuilder.buildClustersLock100)
                    {
                        Parallel.ForEach(new Dictionary<long, List<MyEntity>>(clusters),
                            m_parallelUpdateHandlerAfterSimulation100, WorkPriority.VeryHigh, blocking: true);
                    }
            }
        }
    }
}