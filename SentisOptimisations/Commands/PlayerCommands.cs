using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.Voxels;
using VRage.Network;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [Category("convert")]
    public class PlayerCommands : CommandModule
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();


        [Command("static", ".", null)]
        [Permission(MyPromoteLevel.None)]
        public void ConvertToStatic()
        {
            var player = Context.Player;
            if (player?.Character == null)
                return;
            
            var entitiesInView = GetEntitiesInView(player);
            foreach (var hitInfo in new HashSet<MyPhysics.HitInfo>(entitiesInView))
            {
                if (hitInfo.HkHitInfo.GetHitEntity() is MyCubeGrid grid)
                {
                    if (grid.IsPreview)
                        continue;
                    
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
                    return;
                }
            }
        }
        
        [Command("dynamic", ".", null)]
        [Permission(MyPromoteLevel.None)]
        public void ConvertToDynamic()
        {
            var player = Context.Player;
            if (player?.Character == null)
                return;
            
            var entitiesInView = GetEntitiesInView(player);
            foreach (var hitInfo in new HashSet<MyPhysics.HitInfo>(entitiesInView))
            {
                if (hitInfo.HkHitInfo.GetHitEntity() is MyCubeGrid grid)
                {
                    if (grid.IsPreview)
                        continue;

                    if (!CheckPermissions(grid, player.IdentityId))
                    {
                        Context?.Respond("You have no permission to convert " + grid.DisplayName);
                        return;
                    }
                    if (!grid.IsStatic)
                    {
                        Context?.Respond("Grid " + grid.DisplayName + " already dynamic");
                        return;
                    }
                    if (!ConvertToDynamic(grid))
                    {
                        Context?.Respond("Something went wrong");
                        return;
                    }
                    Context?.Respond("Grid " + grid.DisplayName + " converted to dynamic");
                    return;
                }
            }
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