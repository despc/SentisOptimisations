using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Sandbox.Game.Entities;
using SentisOptimisations;
using VRage.Game.Entity;
using VRageMath;

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

        public static readonly object buildClustersLock = new object();
        public static readonly Random r = new Random();
        public CancellationTokenSource CancellationTokenSource { get; set; }

        public Dictionary<long, HashSet<MyEntity>> Clusters => clusters;

        public void OnLoaded()
        {
            CancellationTokenSource = new CancellationTokenSource();
            // MyEntities.OnEntityAdd += entity => BuildClusters();
            // MyEntities.OnEntityRemove += entity => BuildClusters();
            StartBuildClustersLoop();
        }
        
        public void OnUnloading()
        {
            CancellationTokenSource.Cancel();
        }
        
        private async void StartBuildClustersLoop()
        {
            try
            {
                Log.Info("BuildClustersLoop started");
                while (!CancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(10);
                        await Task.Run(BuildClusters);
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

        private void BuildClusters()
        {
            tmpClusters = new Dictionary<long, HashSet<MyEntity>>();
            HashSet<MyEntity> m_entitiesForUpdate =
                (HashSet<MyEntity>) ReflectionUtils.GetInstanceField(typeof(MyParallelEntityUpdateOrchestrator), MyEntities.Orchestrator,
                    "m_entitiesForUpdate");
            try
            {
                foreach (var myEntity in m_entitiesForUpdate)
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
                    CollectGridsInCluster(newClusterId, topEntity);
                    tmpClusters[newClusterId] = new HashSet<MyEntity>();
                    tmpClusters[newClusterId].Add(myEntity);
                }
                lock (buildClustersLock)
                {
                    clusters = new Dictionary<long, HashSet<MyEntity>>(tmpClusters);
                }
            }
            catch (InvalidOperationException e)
            {
                //Log.Error("Collection is Modified");
            }
            
        }

        private void CollectGridsInCluster(int clusterId, MyEntity e)
        {
            var thisEntityPosition = e.PositionComp.GetPosition();
            var radius = 5000f;
            if (e is MyPlanet)
            {
                radius = radius + ((MyPlanet) e).AtmosphereRadius;
            }

            HashSet<MyEntity> allEntities =
                (HashSet<MyEntity>) ReflectionUtils.GetInstanceField(typeof(MyParallelEntityUpdateOrchestrator), MyEntities.Orchestrator,
                    "m_entitiesForUpdate");
            List<MyEntity> result = new List<MyEntity>();
            foreach (var entity in allEntities)
            {
                if (clustersByGrid.ContainsKey(entity.EntityId))
                {
                    continue;
                }

                var potentialEntityPosition = entity.PositionComp.GetPosition();
                if (entity.EntityId == e.EntityId)
                {
                    continue;
                }
                
                var distance = Vector3D.Distance(thisEntityPosition, potentialEntityPosition);
                if (distance > radius)
                {
                    continue;
                }
                result.Add(entity);
            }
            
            foreach (var entity in new List<MyEntity>(result))
            {
                if (clustersByGrid.ContainsKey(entity.EntityId))
                {
                    continue;
                }

                clustersByGrid[entity.EntityId] = clusterId;
                CollectGridsInCluster(clusterId, entity);
            }
        }
    }
}