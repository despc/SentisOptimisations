using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Scripts.Shared;
using SentisOptimisationsPlugin.AllGridsActions;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Network;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [Category("vs")]
    public class PlayerCommandsVS : CommandModule
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();


        [Command("restore", ".", null)]
        [Permission(MyPromoteLevel.None)]
        public void Restore(string gridName = "")
        {
            try
            {
                var player = Context.Player;
                if (player?.Character == null)
                    return;

                var character = player?.Character;
                Matrix headMatrix = character.GetHeadMatrix(true, true, false);
                Vector3D vector3D = headMatrix.Translation + headMatrix.Forward * 0.5f;
                Vector3D worldEnd = headMatrix.Translation + headMatrix.Forward * 500.5f;
                List<MyPhysics.HitInfo> mRaycastResult = new List<MyPhysics.HitInfo>();
                HashSet<IMyEntity> GridSets = new HashSet<IMyEntity>();
                List<MyCubeGrid> GridsGroup;

                MyCubeGrid gridToRevert = null;
                if (gridName != string.Empty)
                {
                    MyAPIGateway.Entities.GetEntities(GridSets, (IMyEntity Entity) => Entity is IMyCubeGrid &&
                        Entity.DisplayName.Equals(gridName, StringComparison.InvariantCultureIgnoreCase) &&
                        !((MyCubeGrid)Entity).IsPreview);
                    if (!GridSets.Any())
                    {
                        Context.Respond("No such grid exist with name '" + gridName + "' .", "Voxel Shift", "Red");
                        return;
                    }

                    gridToRevert = (MyCubeGrid)GridSets.First();
                }
                else
                {
                    MyPhysics.CastRay(vector3D, worldEnd, mRaycastResult, 15);

                    foreach (var hitInfo in new HashSet<MyPhysics.HitInfo>(mRaycastResult))
                    {
                        if (hitInfo.HkHitInfo.GetHitEntity() is MyCubeGrid grid)
                        {
                            if (grid is null)
                                continue;

                            // ignore projected grid.
                            if (grid.IsPreview)
                                continue;

                            gridToRevert = grid;
                            break;
                        }
                    }
                }

                if (gridToRevert == null)
                {
                    Context.Respond("No grid found", "Voxel Shift", "Red");
                    return;
                }

                // check ownership
                if (gridToRevert.BigOwners.Count > 0 && !gridToRevert.BigOwners.Contains(player.IdentityId))
                {
                    Context.Respond("Only grid owner can restore grid");
                    return;
                }

                if (!Voxels.IsGridInsideVoxel(gridToRevert))
                {
                    Context.Respond("Grid not in voxels!");
                    return;
                }

                var fallInVoxelDetector = AllGridsObserver.FallInVoxelDetector;
                if (fallInVoxelDetector.gridsPos.TryGetValue(gridToRevert.EntityId, out PositionAndOrientation pos))
                {
                    fallInVoxelDetector.DoRestoreSync(gridToRevert, true, pos);
                }
                else
                {
                    MyPlanet planet = null;
                    var currentPosition = gridToRevert.PositionComp.GetPosition();
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

                    var pos1 = planet.GetClosestSurfacePointGlobal(gridToRevert.WorldMatrix.Translation);
                    fallInVoxelDetector.DoRestoreSync(gridToRevert, true,
                        new PositionAndOrientation(pos1, gridToRevert.PositionComp.GetOrientation()));
                }
            }
            catch (Exception e)
            {
                Log.Error("Restore Exception ", e);
            }
        }
    }

    [Category("convert")]
    public class PlayerCommands : CommandModule
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();


        [Command("static", ".", null)]
        [Permission(MyPromoteLevel.None)]
        public void ConvertToStatic(string gridName = "")
        {
            var player = Context.Player;
            if (player?.Character == null)
                return;
            
            if (gridName != string.Empty)
            {
                HashSet<IMyEntity> GridSets = new HashSet<IMyEntity>();
                MyAPIGateway.Entities.GetEntities(GridSets,
                    (IMyEntity Entity) => Entity is IMyCubeGrid &&
                                          Entity.DisplayName.Equals(gridName,
                                              StringComparison.InvariantCultureIgnoreCase));
                foreach (var IEntity in GridSets)
                {
                    if (IEntity is null)
                        continue;
                    DoConvertStatic((MyCubeGrid) IEntity, player);
                    return;
                }
            }
            var entitiesInView = GetEntitiesInView(player);
            foreach (var hitInfo in new HashSet<MyPhysics.HitInfo>(entitiesInView))
            {
                if (hitInfo.HkHitInfo.GetHitEntity() is MyCubeGrid grid)
                {
                    DoConvertStatic(grid, player);
                    return;
                }
            }
        }

        private void DoConvertStatic(MyCubeGrid grid, IMyPlayer player)
        {
            if (grid.IsPreview)
                return;
                    
            if (!CheckPermissions(grid, player.IdentityId))
            {
                Context?.Respond("You have no permission to convert " + grid.DisplayName);
                return;
            }
            // reset velocity
            if (grid.Physics != null)
            {
                grid.Physics.AngularVelocity = new Vector3();
                grid.Physics.LinearVelocity = new Vector3();
            }

            if (grid.IsStatic)
            {
                Context?.Respond("Grid " + grid.DisplayName + " static");
                return;
            }

            if (!ConvertToStatic(grid))
            {
                Context?.Respond("Something went wrong");
                return;
            }

            Context?.Respond("Grid " + grid.DisplayName + " converted to static");
        }

        [Command("dynamic", ".", null)]
        [Permission(MyPromoteLevel.None)]
        public void ConvertToDynamic(string gridName = "")
        {
            var player = Context.Player;
            if (player?.Character == null)
                return;
            
            if (gridName != string.Empty)
            {
                HashSet<IMyEntity> GridSets = new HashSet<IMyEntity>();
                MyAPIGateway.Entities.GetEntities(GridSets,
                    (IMyEntity Entity) => Entity is IMyCubeGrid &&
                                          Entity.DisplayName.Equals(gridName,
                                              StringComparison.InvariantCultureIgnoreCase));
                foreach (var IEntity in GridSets)
                {
                    if (IEntity is null)
                        continue;
                    DoConvertDynamic((MyCubeGrid) IEntity, player);
                    return;
                }
            }
            

            var entitiesInView = GetEntitiesInView(player);
            foreach (var hitInfo in new HashSet<MyPhysics.HitInfo>(entitiesInView))
            {
                if (hitInfo.HkHitInfo.GetHitEntity() is MyCubeGrid grid)
                {
                    DoConvertDynamic(grid, player);
                    return;
                }
            }
        }

        private void DoConvertDynamic(MyCubeGrid grid, IMyPlayer player)
        {
            if (grid.IsPreview)
                return; // continue;

            if (!CheckPermissions(grid, player.IdentityId))
            {
                Context?.Respond("You have no permission to convert " + grid.DisplayName);
                return; // return;
            }

            if (!grid.IsStatic)
            {
                Context?.Respond("Grid " + grid.DisplayName + " already dynamic");
                return; // return;
            }

            if (!ConvertToDynamic(grid))
            {
                Context?.Respond("Something went wrong");
                return; // return;
            }

            Context?.Respond("Grid " + grid.DisplayName + " converted to dynamic");
        }


        private static List<MyPhysics.HitInfo> GetEntitiesInView(IMyPlayer player)
        {
            Matrix headMatrix = player.Character.GetHeadMatrix(true, true, false);
            Vector3D vector3D = headMatrix.Translation + headMatrix.Forward * 0.5f;
            Vector3D worldEnd = headMatrix.Translation + headMatrix.Forward * 500.5f;
            List<MyPhysics.HitInfo> mRaycastResult = new List<MyPhysics.HitInfo>();
            MyPhysics.CastRay(vector3D, worldEnd, mRaycastResult, 15);
            return mRaycastResult;
        }

        private bool ConvertToStatic(MyCubeGrid grid)
        {
            try
            {
                grid.Physics?.SetSpeeds(Vector3.Zero, Vector3.Zero);
                grid.ConvertToStatic();
                try
                {
                    MyMultiplayer.RaiseEvent(grid, x => x.ConvertToStatic);
                    foreach (var player in MySession.Static.Players.GetOnlinePlayers())
                    {
                        MyMultiplayer.RaiseEvent(grid, x => x.ConvertToStatic, new EndpointId(player.Id.SteamId));
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "()Exception in RaiseEvent.");
                }

                return true;
            }
            catch (Exception e)
            {
                return false;
            }
        }

        private static bool CheckPermissions(MyCubeGrid grid, long playerIdentity)
        {
            if (playerIdentity == 0L || grid.BigOwners.Count == 0)
            {
                return true;
            }

            foreach (long bigOwner in grid.BigOwners)
            {
                switch (MyIDModule.GetRelationPlayerBlock(bigOwner, playerIdentity, MyOwnershipShareModeEnum.Faction))
                {
                    case MyRelationsBetweenPlayerAndBlock.NoOwnership:
                    case MyRelationsBetweenPlayerAndBlock.Owner:
                    case MyRelationsBetweenPlayerAndBlock.FactionShare:
                        return true;
                    default:
                        return false;
                }
            }

            return false;
        }
        private bool ConvertToDynamic(MyCubeGrid grid)
        {
            try
            {
                grid.OnConvertToDynamic();
                return true;
            }
            catch (Exception e)
            {
                return false;
            }
        }
    }
}