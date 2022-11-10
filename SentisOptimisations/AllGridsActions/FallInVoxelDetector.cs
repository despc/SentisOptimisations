using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using Scripts.Shared;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisOptimisationsPlugin.AllGridsActions
{
    public class FallInVoxelDetector
    {
        private Dictionary<long, PositionAndOrientation> gridsPos = new Dictionary<long, PositionAndOrientation>();

        public void SavePos(MyCubeGrid grid)
        {
            gridsPos[grid.EntityId] =
                new PositionAndOrientation(grid.PositionComp.GetPosition(), grid.PositionComp.GetOrientation());
        }

        public void RestorePos(MyCubeGrid grid)
        {
            if (gridsPos.TryGetValue(grid.EntityId, out PositionAndOrientation pos))
            {
                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    try
                    {
                        
                        var grids = grid.GetConnectedGrids(GridLinkTypeEnum.Physical);
                        var aabb = new BoundingBoxD(grid.PositionComp.WorldAABB.Min, grid.PositionComp.WorldAABB.Max);
                        foreach (var g in grids)
                        {
                            aabb.Include(g.PositionComp.WorldAABB);
                        }

                        MyPlanet planet = null;
                        var currentPosition = grid.PositionComp.GetPosition();
                        foreach (var p in AllGridsObserver.Planets)
                        {
                            if (planet == null)
                            {
                                planet = p;
                                continue;
                            }

                            if (Vector3D.Distance(planet.PositionComp.GetPosition(), currentPosition) >
                                Vector3D.Distance(p.PositionComp.GetPosition(), currentPosition))
                            {
                                planet = p;
                            }
                        }
                        //var pos = planet.GetClosestSurfacePointGlobal(cockpit.WorldMatrix.Translation);
                        foreach (var g in grids)
                        {
                            g.Physics.AngularVelocity = Vector3.Zero;
                            g.Physics.LinearVelocity = Vector3.Zero;
                        }

                        var vec = (pos.Position - planet.PositionComp.WorldMatrix.Translation);
                        vec.Normalize();
                        currentPosition = pos.Position + vec * (aabb.Size.Max()+10);

                        var m = grid.WorldMatrix;
                        m.Translation = currentPosition;
            
                        //grid.WorldMatrix = m;
                        //grid.PositionComp.SetPosition(currentPosition);
                        grid.Teleport(m);
                        SentisOptimisationsPlugin.Log.Warn("Restored from voxels grid " + grid.DisplayName);
                    }
                    catch (Exception e)
                    {
                        SentisOptimisationsPlugin.Log.Warn("RestorePos Prevent crash", e);
                    }
                });
            }
        }

        public void CheckAndSavePos(MyCubeGrid grid)
        {
            if (grid == null || grid.MarkedForClose || grid.Physics == null || grid.Physics.IsStatic)
            {
                return;
            }

            if (Voxels.IsGridInsideVoxel(grid))
            {
                RestorePos(grid);
                return;
            }

            SavePos(grid);
        }
    }

    internal class PositionAndOrientation
    {
        private Vector3D _position;
        private MatrixD _orientation;

        public PositionAndOrientation(Vector3D position, MatrixD orientation)
        {
            _position = position;
            _orientation = orientation;
        }

        public Vector3D Position
        {
            get => _position;
            set => _position = value;
        }

        public MatrixD Orientation
        {
            get => _orientation;
            set => _orientation = value;
        }
    }
}