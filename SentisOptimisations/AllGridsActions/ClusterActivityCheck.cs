using System;
using System.Collections.Generic;
using NLog;
using Sandbox.Engine.Physics;
using Sandbox.Game.World;
using SentisOptimisations;
using VRage.Collections;
using VRageMath;
using VRageMath.Spatial;

namespace SentisOptimisationsPlugin.AllGridsActions
{
    public class ClusterActivityCheck
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static readonly HashSet<int> ActiveClusters = new HashSet<int>();

        public void CheckClusters()
        {
            try
            {
                if (!SentisOptimisationsPlugin.Config.PatchClusterActivity)
                {
                    return;
                }

                Dictionary<int, BoundingBoxD> clustersSize = new Dictionary<int, BoundingBoxD>();
                var myPhysics = MySession.Static.GetComponent<MyPhysics>();
                ListReader<MyClusterTree.MyCluster> clusters = MyPhysics.Clusters.GetClusters();
                foreach (var myCluster in clusters)
                {
                    clustersSize[myCluster.ClusterId] = myCluster.AABB;
                }

                ActiveClusters.Clear();
                foreach (var p in PlayerUtils.GetAllPlayers())
                {
                    if (p.IsBot)
                    {
                        continue;
                    }

                    if (p.Character == null)
                    {
                        continue;
                    }
                    foreach (var pair in clustersSize)
                    {
                        BoundingSphereD sphere = new BoundingSphereD(p.Character.GetPosition(), 6000);
                        var containmentType = pair.Value.Contains(sphere);
                        if (containmentType == ContainmentType.Contains || containmentType == ContainmentType.Intersects)
                        {
                            ActiveClusters.Add(pair.Key);
                        }
                    }
                }

                foreach (var activeCluster in ActiveClusters)
                {
                    clustersSize.Remove(activeCluster);
                }

                
            }
            catch (Exception e)
            {
                Log.Error("ClusterActivityCheck exception ", e);
            }
        }
    }
}