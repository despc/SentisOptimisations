using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using SentisOptimisations;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Collections;
using VRage.Game.Entity;
using VRageMath;
// ReSharper disable ArrangeTypeMemberModifiers
// ReSharper disable InconsistentNaming

namespace SentisOptimisationsPlugin.Clusters
{
    public class ClusterBuilder
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        
        //         <ид грида, ид кластера>
        Dictionary<long, int> clustersByGrid = new Dictionary<long, int>();

        //         <ид кластера, список сущностей>
        Dictionary<long, HashSet<MyEntity>> clusters = new Dictionary<long, HashSet<MyEntity>>();
        Dictionary<long, HashSet<MyEntity>> tmpClusters = new Dictionary<long, HashSet<MyEntity>>();
        HashSet<MyEntity> forSerialUpdate = new HashSet<MyEntity>();
        HashSet<MyEntity> tmpforSerialUpdate = new HashSet<MyEntity>();
        
        //         <ид грида, ид кластера>
        Dictionary<long, int> clustersByGrid10 = new Dictionary<long, int>();

        //         <ид кластера, список сущностей>
        Dictionary<long, HashSet<MyEntity>> clusters10 = new Dictionary<long, HashSet<MyEntity>>();
        Dictionary<long, HashSet<MyEntity>> tmpClusters10 = new Dictionary<long, HashSet<MyEntity>>();
        HashSet<MyEntity> forSerialUpdate10 = new HashSet<MyEntity>();
        HashSet<MyEntity> tmpforSerialUpdate10 = new HashSet<MyEntity>();
        //         <ид грида, ид кластера>
        Dictionary<long, int> clustersByGrid100 = new Dictionary<long, int>();

        //         <ид кластера, список сущностей>
        Dictionary<long, HashSet<MyEntity>> clusters100 = new Dictionary<long, HashSet<MyEntity>>();
        Dictionary<long, HashSet<MyEntity>> tmpClusters100 = new Dictionary<long, HashSet<MyEntity>>();
        HashSet<MyEntity> forSerialUpdate100 = new HashSet<MyEntity>();
        HashSet<MyEntity> tmpforSerialUpdate100 = new HashSet<MyEntity>();
        
        public static readonly object buildClustersLock = new object();
        public static readonly object buildClustersLock10 = new object();
        public static readonly object buildClustersLock100 = new object();
        public static readonly Random r = new Random();
        public CancellationTokenSource CancellationTokenSource { get; set; }

        public Dictionary<long, HashSet<MyEntity>> Clusters => clusters;
        public Dictionary<long, HashSet<MyEntity>> Clusters10 => clusters10;
        public Dictionary<long, HashSet<MyEntity>> Clusters100 => clusters100;

        public HashSet<MyEntity> ForSerialUpdate => forSerialUpdate;

        public HashSet<MyEntity> ForSerialUpdate10 => forSerialUpdate10;

        public HashSet<MyEntity> ForSerialUpdate100 => forSerialUpdate100;

        public void OnLoaded()
        {
            CancellationTokenSource = new CancellationTokenSource();
            // MyEntities.OnEntityAdd += entity => BuildClusters();
            // MyEntities.OnEntityRemove += entity => BuildClusters();
            StartBuildClustersLoop();
            StartBuildClustersLoop10();
            StartBuildClustersLoop100();
        }
        
        public void OnUnloading()
        {
            CancellationTokenSource.Cancel();
        }
        
        private async void StartBuildClustersLoop()
        {
            try
            {
                await Task.Delay(10000);
                Log.Info("BuildClustersLoop started");
                while (!CancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(15);
                        await Task.Run(BuildClusters);
                    }
                    catch (ArgumentException e)
                    {
                       // Log.Error("BuildClustersLoop Error", e);
                    }
                    catch (Exception e)
                    {
                        Log.Error("BuildClustersLoop Error", e);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("BuildClustersLoop start Error", e);
            }
        }
        
        private async void StartBuildClustersLoop10()
        {
            try
            {
                await Task.Delay(10000);
                Log.Info("BuildClustersLoop10 started");
                while (!CancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(100);
                        await Task.Run(BuildClusters10);
                    }
                    catch (ArgumentException e)
                    {
                        // Log.Error("BuildClustersLoop Error", e);
                    }
                    catch (Exception e)
                    {
                        Log.Error("BuildClustersLoop10 Error", e);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("BuildClustersLoop10 start Error", e);
            }
        }
        
        private async void StartBuildClustersLoop100()
        {
            try
            {
                await Task.Delay(10000);
                Log.Info("BuildClustersLoop100 started");
                while (!CancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(300);
                        await Task.Run(BuildClusters100);
                    }
                    catch (ArgumentException e)
                    {
                        // Log.Error("BuildClustersLoop Error", e);
                    }
                    catch (Exception e)
                    {
                        Log.Error("BuildClustersLoop100 Error", e);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("BuildClustersLoop100 start Error", e);
            }
        }

        private void BuildClusters()
        {
            tmpClusters = new Dictionary<long, HashSet<MyEntity>>();
            tmpforSerialUpdate = new HashSet<MyEntity>();
            clustersByGrid = new Dictionary<long, int>();
            HashSet<MyEntity> m_entitiesForUpdate =
                (HashSet<MyEntity>) ReflectionUtils.GetInstanceField(typeof(MyParallelEntityUpdateOrchestrator), MyEntities.Orchestrator,
                    "m_entitiesForUpdate");
            var startNew = Stopwatch.StartNew();
            try
            {
                foreach (var myEntity in new List<MyEntity>(m_entitiesForUpdate))
                {
                    if (ToSerialUpdate(myEntity))
                    {
                        continue;
                    }
                    
                    MyEntity topEntity;
                    if (myEntity is MyCubeBlock)
                    {
                        var grid = ((MyCubeBlock) myEntity).CubeGrid;
                        topEntity = grid;
                    }
                    else
                    {
                        topEntity = myEntity;
                    }
                    if (topEntity == null)
                    {
                        continue;
                    }
                    var entityId = topEntity.EntityId;

                        
                    if (clustersByGrid.ContainsKey(entityId))
                    {
                        var clusterId = clustersByGrid[entityId];
                        if (!tmpClusters.ContainsKey(clusterId))
                        {
                            tmpClusters[clusterId] = new HashSet<MyEntity>();
                        }
                        tmpClusters[clusterId].Add(myEntity);
                        continue;
                    }

                    var newClusterId = r.Next(-2000000000, 2000000000);
                    clustersByGrid[entityId] = newClusterId;
                    CollectGridsInCluster(newClusterId, topEntity, new List<MyEntity>(m_entitiesForUpdate), ref clustersByGrid);
                    tmpClusters[newClusterId] = new HashSet<MyEntity>();
                    tmpClusters[newClusterId].Add(myEntity);
                }

                var startNewElapsedMilliseconds = startNew.ElapsedMilliseconds;
                //Log.Error("BuildClusters Run for " + startNew.ElapsedMilliseconds);
                lock (buildClustersLock)
                {
                    clusters = new Dictionary<long, HashSet<MyEntity>>(tmpClusters);
                    forSerialUpdate = new HashSet<MyEntity>(tmpforSerialUpdate);
                }
            }
            catch (InvalidOperationException e)
            {
                //Log.Error("Collection is Modified");
            }
            
        }
        private bool ToSerialUpdate(MyEntity myEntity)
        {
            if (myEntity is MyVoxelBase )
            {
                tmpforSerialUpdate.Add(myEntity);
                return true;
            }

            return false;
        }
        private void BuildClusters10()
        {
            tmpClusters10 = new Dictionary<long, HashSet<MyEntity>>();
            tmpforSerialUpdate10 = new HashSet<MyEntity>();
            clustersByGrid10 = new Dictionary<long, int>();
            MyDistributedUpdater<List<MyEntity>, MyEntity> m_entitiesForUpdate10 =
                (MyDistributedUpdater<List<MyEntity>, MyEntity>) ReflectionUtils.GetInstanceField(typeof(MyParallelEntityUpdateOrchestrator), MyEntities.Orchestrator,
                    "m_entitiesForUpdate10");
            var startNew = Stopwatch.StartNew();
            
            try
            {
                foreach (var myEntity in new List<MyEntity>(m_entitiesForUpdate10.List))
                {
                    MyEntity topEntity;

                    if (ToSerialUpdate10(myEntity))
                    {
                        continue;
                    }
                    if (myEntity is MyCubeBlock)
                    {
                        var grid = ((MyCubeBlock) myEntity).CubeGrid;
                        topEntity = grid;
                    }
                    else
                    {
                        topEntity = myEntity;
                    }
                    var entityId = topEntity.EntityId;

                        
                    if (clustersByGrid10.ContainsKey(entityId))
                    {
                        var clusterId = clustersByGrid10[entityId];
                        if (!tmpClusters10.ContainsKey(clusterId))
                        {
                            tmpClusters10[clusterId] = new HashSet<MyEntity>();
                        }
                        tmpClusters10[clusterId].Add(myEntity);
                        continue;
                    }

                    var newClusterId = r.Next(-2000000000, 2000000000);
                    clustersByGrid10[entityId] = newClusterId;
                    CollectGridsInCluster(newClusterId, topEntity, m_entitiesForUpdate10.List,  ref clustersByGrid10);
                    tmpClusters10[newClusterId] = new HashSet<MyEntity>();
                    tmpClusters10[newClusterId].Add(myEntity);
                }
                var startNewElapsedMilliseconds = startNew.ElapsedMilliseconds;
                //Log.Error("BuildClusters10 Run for " + startNew.ElapsedMilliseconds);
                lock (buildClustersLock10)
                {
                    
                    clusters10 = new Dictionary<long, HashSet<MyEntity>>(tmpClusters10);
                    forSerialUpdate10 = new HashSet<MyEntity>(tmpforSerialUpdate10);
                }
            }
            catch (InvalidOperationException e)
            {
                //Log.Error("Collection is Modified");
            }
            
        }

        private bool ToSerialUpdate10(MyEntity myEntity)
        {
            if (myEntity is MySensorBlock || myEntity is MyAssembler)
            {
                tmpforSerialUpdate10.Add(myEntity);
                return true;
            }
            if (myEntity is MyFunctionalBlock)
            {
                return false;
            }
            tmpforSerialUpdate10.Add(myEntity);
            return true;
        }
        

        private void BuildClusters100()
        {
            tmpClusters100 = new Dictionary<long, HashSet<MyEntity>>();
            clustersByGrid100 = new Dictionary<long, int>();
            tmpforSerialUpdate100 = new HashSet<MyEntity>();
            MyDistributedUpdater<List<MyEntity>, MyEntity> m_entitiesForUpdate100 =
                (MyDistributedUpdater<List<MyEntity>, MyEntity>) ReflectionUtils.GetInstanceField(typeof(MyParallelEntityUpdateOrchestrator), MyEntities.Orchestrator,
                    "m_entitiesForUpdate100");
            var startNew = Stopwatch.StartNew();

            var thrusterClusterId = r.Next(-2000000000, 2000000000);
            tmpClusters100[thrusterClusterId] = new HashSet<MyEntity>();

            try
            {
                foreach (var myEntity in new List<MyEntity>(m_entitiesForUpdate100.List))
                {
                    MyEntity topEntity;
                    if (myEntity is MyThrust)
                    {
                        tmpClusters100[thrusterClusterId].Add(myEntity);
                        clustersByGrid100[myEntity.EntityId] = thrusterClusterId;
                        continue;
                    }
                    if (ToSerialUpdate100(myEntity))
                    {
                        continue;
                    }
                    if (myEntity is MyCubeBlock)
                    {
                        var grid = ((MyCubeBlock) myEntity).CubeGrid;
                        topEntity = grid;
                    }
                    else
                    {
                        topEntity = myEntity;
                    }
                    var entityId = topEntity.EntityId;

                        
                    if (clustersByGrid100.ContainsKey(entityId))
                    {
                        var clusterId = clustersByGrid100[entityId];
                        if (!tmpClusters100.ContainsKey(clusterId))
                        {
                            tmpClusters100[clusterId] = new HashSet<MyEntity>();
                        }
                        tmpClusters100[clusterId].Add(myEntity);
                        continue;
                    }

                    var newClusterId = r.Next(-2000000000, 2000000000);
                    clustersByGrid100[entityId] = newClusterId;
                    CollectGridsInCluster(newClusterId, topEntity, m_entitiesForUpdate100.List, ref clustersByGrid100);
                    tmpClusters100[newClusterId] = new HashSet<MyEntity>();
                    tmpClusters100[newClusterId].Add(myEntity);
                }
                var startNewElapsedMilliseconds = startNew.ElapsedMilliseconds;
                //Log.Error("BuildClusters100 Run for " + startNew.ElapsedMilliseconds);
                lock (buildClustersLock100)
                {
                    clusters100 = new Dictionary<long, HashSet<MyEntity>>(tmpClusters100);
                    forSerialUpdate100 = new HashSet<MyEntity>(tmpforSerialUpdate100);
                }
            }
            catch (InvalidOperationException e)
            {
                //Log.Error("Collection is Modified");
            }
            
        }
        private bool ToSerialUpdate100(MyEntity myEntity)
        {
            if (myEntity is MyGasGenerator ||
                myEntity is MyBatteryBlock ||
                myEntity is MyAirVent )
            {
                return false;
            }
            tmpforSerialUpdate100.Add(myEntity);
            return true;
            
        }

        private void CollectGridsInCluster(int clusterId, MyEntity e, ICollection<MyEntity> allEntities, ref Dictionary<long, int> clustersByGridfc)
        {
            var thisEntityPosition = e.PositionComp.GetPosition();
            var radius = 51000f;
            if (e is MyPlanet)
            {
                radius = radius + ((MyPlanet)e).MaximumRadius;
            }


            
            MyDynamicAABBTreeD m_dynamicObjectsTree = (MyDynamicAABBTreeD) ReflectionUtils.GetPrivateStaticField(typeof(MyGamePruningStructure), "m_dynamicObjectsTree");
            MyDynamicAABBTreeD m_staticObjectsTree = (MyDynamicAABBTreeD) ReflectionUtils.GetPrivateStaticField(typeof(MyGamePruningStructure), "m_staticObjectsTree");
            BoundingSphereD boundingSphere = new BoundingSphereD(thisEntityPosition, radius);
            List<MyEntity> result = new List<MyEntity>();
            m_dynamicObjectsTree.OverlapAllBoundingSphere(ref boundingSphere, result);
            m_staticObjectsTree.OverlapAllBoundingSphere(ref boundingSphere, result);
            
            // foreach (var entity in allEntities)
            // {
            //     if (entity is MyVoxelBase)
            //     {
            //         continue;
            //     }
            //     if (clustersByGridfc.ContainsKey(entity.EntityId))
            //     {
            //         continue;
            //     }
            //
            //     var potentialEntityPosition = entity.PositionComp.GetPosition();
            //     if (entity.EntityId == e.EntityId)
            //     {
            //         continue;
            //     }
            //     
            //     var distance = Vector3D.Distance(thisEntityPosition, potentialEntityPosition);
            //     if (distance > radius)
            //     {
            //         continue;
            //     }
            //     result.Add(entity);
            // }
            
            foreach (var entity in new List<MyEntity>(result))
            {

                MyEntity topEntity;
                if (entity.EntityId == e.EntityId)
                {
                    continue;
                }
                
                if (entity is MyCubeBlock)
                {
                    var grid = ((MyCubeBlock) entity).CubeGrid;
                    topEntity = grid;
                }
                else
                {
                    topEntity = entity;
                }
                var entityId = topEntity.EntityId;
                if (clustersByGridfc.ContainsKey(entityId))
                {
                    continue;
                }
                clustersByGridfc[entityId] = clusterId;
                allEntities.Remove(entity);
                CollectGridsInCluster(clusterId, entity, allEntities,ref clustersByGridfc);
            }
        }
    }
}