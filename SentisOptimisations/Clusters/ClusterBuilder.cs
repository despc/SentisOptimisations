using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
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
        Dictionary<long, List<MyEntity>> clusters = new Dictionary<long, List<MyEntity>>();
        Dictionary<long, List<MyEntity>> tmpClusters = new Dictionary<long, List<MyEntity>>();

        //         <ид грида, ид кластера>
        Dictionary<long, int> clustersByGrid10 = new Dictionary<long, int>();

        //         <ид кластера, список сущностей>
        Dictionary<long, List<MyEntity>> clusters10 = new Dictionary<long, List<MyEntity>>();
        Dictionary<long, List<MyEntity>> tmpClusters10 = new Dictionary<long, List<MyEntity>>();


        //         <ид грида, ид кластера>
        Dictionary<long, int> clustersByGrid100 = new Dictionary<long, int>();

        //         <ид кластера, список сущностей>
        Dictionary<long, List<MyEntity>> clusters100 = new Dictionary<long, List<MyEntity>>();
        Dictionary<long, List<MyEntity>> tmpClusters100 = new Dictionary<long, List<MyEntity>>();


        public static readonly object buildClustersLock = new object();
        public static readonly object buildClustersLock10 = new object();
        public static readonly object buildClustersLock100 = new object();
        public static readonly Random r = new Random();
        public CancellationTokenSource CancellationTokenSource { get; set; }

        public Dictionary<long, List<MyEntity>> Clusters => clusters;
        public Dictionary<long, List<MyEntity>> Clusters10 => clusters10;
        public Dictionary<long, List<MyEntity>> Clusters100 => clusters100;

        public long ClusterTime = 0;
        public long ClusterTime10 = 0;
        public long ClusterTime100 = 0;

        
        public static HashSet<MyEntity> m_entitiesForUpdate = new HashSet<MyEntity>();
        public static MyDistributedUpdater<List<MyEntity>, MyEntity> m_entitiesForUpdate10 = new MyDistributedUpdater<List<MyEntity>, MyEntity>(10);
        public static MyDistributedUpdater<List<MyEntity>, MyEntity> m_entitiesForUpdate100 = new MyDistributedUpdater<List<MyEntity>, MyEntity>(100);
        
        public static HashSet<long> m_entitiesForUpdateId = new HashSet<long>();
        public static HashSet<long> m_entitiesForUpdate10Id = new HashSet<long>();
        public static HashSet<long> m_entitiesForUpdate100Id = new HashSet<long>();
        
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
                        // await Task.Delay(SentisOptimisationsPlugin.Config.Cluster1BuildDelay);
                        if (!SentisOptimisationsPlugin.Config.ClustersEnabled)
                        {
                            continue;
                        }
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
                        // await Task.Delay(SentisOptimisationsPlugin.Config.Cluster10BuildDelay);
                        if (!SentisOptimisationsPlugin.Config.ClustersEnabled)
                        {
                            continue;
                        }
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
                        // await Task.Delay(SentisOptimisationsPlugin.Config.Cluster100BuildDelay);
                        if (!SentisOptimisationsPlugin.Config.ClustersEnabled)
                        {
                            continue;
                        }
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
            tmpClusters = new Dictionary<long, List<MyEntity>>();
            clustersByGrid = new Dictionary<long, int>();
            
            var startNew = Stopwatch.StartNew();
            try
            {
                foreach (var myEntity in new List<MyEntity>(m_entitiesForUpdate))
                {
                    // if (ToSerialUpdate(myEntity))
                    // {
                    //     continue;
                    // }

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
                            tmpClusters[clusterId] = new List<MyEntity>();
                        }

                        tmpClusters[clusterId].Add(myEntity);
                        continue;
                    }

                    var newClusterId = r.Next(-2000000000, 2000000000);
                    clustersByGrid[entityId] = newClusterId;
                    CollectGridsInCluster(newClusterId, topEntity, ref clustersByGrid, new HashSet<long>());
                    tmpClusters[newClusterId] = new List<MyEntity>();
                    tmpClusters[newClusterId].Add(myEntity);
                }
                lock (buildClustersLock)
                {
                    clusters = new Dictionary<long, List<MyEntity>>(tmpClusters);
                }
                ClusterTime = startNew.ElapsedMilliseconds;
                if (ClusterTime < 16.6)
                {
                    Thread.Sleep((int) (16.6 - ClusterTime));
                }
            }
            catch (InvalidOperationException e)
            {
               // Log.Error("Collection is Modified");
            }
        }


        public static bool IsForSerialUpdate(MyEntity myEntity)
        {
            if (SentisOptimisationsPlugin.Config.ClustersParallelGas && (myEntity is MyGasGenerator || myEntity is MyGasTank))
            {
                return false;
            }

            if (SentisOptimisationsPlugin.Config.ClustersParallelWeapons && myEntity is MyLargeTurretBase)
            {
                return false;
            }

            if (SentisOptimisationsPlugin.Config.ClustersParallelDrill && (myEntity is MyShipDrill || myEntity is MyShipConnector))
            {
                return false;
            }

            if (SentisOptimisationsPlugin.Config.ClustersParallelWelders && myEntity is MyShipWelder)
            {
                return false;
            }

            if (SentisOptimisationsPlugin.Config.ClustersParallelGrinders && myEntity is MyShipGrinder)
            {
                return false;
            }

            if (SentisOptimisationsPlugin.Config.ClustersParallelProduction &&
                (myEntity is MyAssembler || myEntity is MyRefinery))
            {
                return false;
            }

            return true;
        }

        private void BuildClusters10()
        {
            tmpClusters10 = new Dictionary<long, List<MyEntity>>();
            clustersByGrid10 = new Dictionary<long, int>();
            var sw = Stopwatch.StartNew();

            try
            {
                m_entitiesForUpdate10.Update();
                HashSet<long> toExclude = new HashSet<long>();
                HashSet<MyEntity> toUpdate = new HashSet<MyEntity>();
                foreach (var myEntity in m_entitiesForUpdate10)
                {
                    if (myEntity is MyCubeBlock)
                    {
                        var grid = ((MyCubeBlock) myEntity).CubeGrid;
                        toExclude.Add(grid.EntityId);
                        toUpdate.Add(myEntity);
                    }
                }

                foreach (var myEntity in toUpdate)
                {
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

                    var entityId = topEntity.EntityId;


                    if (clustersByGrid10.ContainsKey(entityId))
                    {
                        var clusterId = clustersByGrid10[entityId];
                        if (!tmpClusters10.ContainsKey(clusterId))
                        {
                            tmpClusters10[clusterId] = new List<MyEntity>();
                        }

                        tmpClusters10[clusterId].Add(myEntity);
                        continue;
                    }

                    var newClusterId = r.Next(-2000000000, 2000000000);
                    clustersByGrid10[entityId] = newClusterId;
                    CollectGridsInCluster(newClusterId, topEntity, ref clustersByGrid10, toExclude);
                    tmpClusters10[newClusterId] = new List<MyEntity>();
                    tmpClusters10[newClusterId].Add(myEntity);
                }

                lock (buildClustersLock10)
                {
                    clusters10 = new Dictionary<long, List<MyEntity>>(tmpClusters10);
                }
                ClusterTime10 = sw.ElapsedMilliseconds;
                if (ClusterTime10 < 16.6)
                {
                    Thread.Sleep((int) (16.6 - ClusterTime100));
                }
            }
            catch (InvalidOperationException e)
            {
                //Log.Error("Collection is Modified");
            }
        }


        private void BuildClusters100()
        {
            tmpClusters100 = new Dictionary<long, List<MyEntity>>();
            clustersByGrid100 = new Dictionary<long, int>();
            var sw = Stopwatch.StartNew();

            var thrusterClusterId = r.Next(-2000000000, 2000000000);
            tmpClusters100[thrusterClusterId] = new List<MyEntity>();

            try
            {
                m_entitiesForUpdate100.Update();
                
                HashSet<long> toExclude = new HashSet<long>();
                HashSet<MyEntity> toUpdate = new HashSet<MyEntity>();
                foreach (var myEntity in m_entitiesForUpdate100)
                {
                    if (myEntity is MyCubeBlock)
                    {
                        toUpdate.Add(myEntity);
                        var grid = ((MyCubeBlock) myEntity).CubeGrid;
                        toExclude.Add(grid.EntityId);
                    }
                }

                foreach (var myEntity in toUpdate)
                {
                    MyEntity topEntity;
                    if (myEntity is MyThrust)
                    {
                        tmpClusters100[thrusterClusterId].Add(myEntity);
                        clustersByGrid100[myEntity.EntityId] = thrusterClusterId;
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
                            tmpClusters100[clusterId] = new List<MyEntity>();
                        }

                        tmpClusters100[clusterId].Add(myEntity);
                        continue;
                    }

                    var newClusterId = r.Next(-2000000000, 2000000000);
                    clustersByGrid100[entityId] = newClusterId;
                    CollectGridsInCluster(newClusterId, topEntity, ref clustersByGrid100, toExclude);
                    tmpClusters100[newClusterId] = new List<MyEntity>();
                    tmpClusters100[newClusterId].Add(myEntity);
                }

                lock (buildClustersLock100)
                {
                    clusters100 = new Dictionary<long, List<MyEntity>>(tmpClusters100);
                }
                ClusterTime100 = sw.ElapsedMilliseconds;
                if (ClusterTime100 < 16.6)
                {
                    Thread.Sleep((int) (16.6 - ClusterTime100));
                }
            }
            catch (InvalidOperationException e)
            {
                //Log.Error("Collection is Modified");
            }
        }

        private void CollectGridsInCluster(int clusterId, MyEntity e, ref Dictionary<long, int> clustersByGridfc,
            HashSet<long> toUpdate)
        {
            var thisEntityPosition = e.PositionComp.GetPosition();
            var radius = SentisOptimisationsPlugin.Config.ClusterRadius;
            if (e is MyPlanet)
            {
                radius = radius + ((MyPlanet) e).MaximumRadius;
            }

            MyDynamicAABBTreeD m_dynamicObjectsTree =
                (MyDynamicAABBTreeD) ReflectionUtils.GetPrivateStaticField(typeof(MyGamePruningStructure),
                    "m_dynamicObjectsTree");
            MyDynamicAABBTreeD m_staticObjectsTree =
                (MyDynamicAABBTreeD) ReflectionUtils.GetPrivateStaticField(typeof(MyGamePruningStructure),
                    "m_staticObjectsTree");
            BoundingSphereD boundingSphere = new BoundingSphereD(thisEntityPosition, radius);
            List<MyEntity> result = new List<MyEntity>();
            m_dynamicObjectsTree.OverlapAllBoundingSphere(ref boundingSphere, result);
            m_staticObjectsTree.OverlapAllBoundingSphere(ref boundingSphere, result);

            foreach (var entity in new List<MyEntity>(result))
            {
                MyEntity topEntity;
                if (toUpdate.Count > 0 && !toUpdate.Contains(entity.EntityId))
                {
                    continue;
                }
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
                CollectGridsInCluster(clusterId, entity, ref clustersByGridfc, toUpdate);
            }
        }
    }
}