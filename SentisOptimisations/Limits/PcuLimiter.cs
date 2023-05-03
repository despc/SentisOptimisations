using System;
using System.Collections.Generic;
using NLog;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using SentisOptimisations;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    public class PcuLimiter
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        static HashSet<long> gridsOverlimit = new HashSet<long>();


        public void CheckGrid(MyCubeGrid grid)
        {
            if (grid.IsStatic || IsLimitNotReached(grid)) return;
            MyAPIGateway.Utilities.InvokeOnGameThread(() => { LimitReached(grid); });
        }

        private void MyCubeGridsOnBlockDestroyed(MyCubeGrid cube, MySlimBlock block)
        {
            if (!SentisOptimisationsPlugin.Config.EnabledPcuLimiter || cube == null || block == null ||
                block.FatBlock == null)
                return;

            if (gridsOverlimit.Contains(cube.EntityId))
            {
                if (IsLimitNotReached(cube))
                {
                    LimitNotReached(cube);
                }
            }
        }

        public void LimitNotReached(MyCubeGrid cube)
        {
            if (gridsOverlimit.Contains(cube.EntityId))
            {
                gridsOverlimit.Remove(cube.EntityId);
            }

            List<IMySlimBlock> blocks = GridUtils.GetBlocks<IMyFunctionalBlock>((IMyCubeGrid)cube);
            foreach (var mySlimBlock in blocks)
            {
                if (mySlimBlock.FatBlock is MyReactor ||
                    mySlimBlock.FatBlock is MyMedicalRoom ||
                    mySlimBlock.FatBlock is MySurvivalKit ||
                    mySlimBlock.FatBlock is MyProjectorBase ||
                    mySlimBlock.FatBlock is MyShipConnector ||
                    mySlimBlock.FatBlock is MySafeZoneBlock)
                {
                    continue;
                }

                try
                {
                    ((IMyFunctionalBlock)mySlimBlock.FatBlock).Enabled = true;
                }
                catch (Exception e)
                {
                    Log.Error("Set Enabled exception", e);
                }
            }


            var subGrids = GridUtils.GetSubGrids((IMyCubeGrid)cube);
            foreach (var myCubeGrid in subGrids)
            {
                if (gridsOverlimit.Contains(myCubeGrid.EntityId))
                {
                    gridsOverlimit.Remove(myCubeGrid.EntityId);
                }

                List<IMySlimBlock> subGridBlocks = GridUtils.GetBlocks<IMyFunctionalBlock>(myCubeGrid);
                foreach (var mySlimBlock in subGridBlocks)
                {
                    if (noDisableBlock(mySlimBlock))
                    {
                        continue;
                    }
                }
            }
        }

        private void MyCubeGrids_BlockBuilt(MyCubeGrid cube, MySlimBlock block)
        {
            if (!SentisOptimisationsPlugin.Config.EnabledPcuLimiter || cube == null || block == null ||
                block.FatBlock == null)
                return;
            try
            {
                if (block.FatBlock.IsFunctional)
                {
                    if (IsLimitNotReached(cube))
                        return;
                    LimitReached(cube);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        private void MyCubeGrids_BlockFunctional(MyCubeGrid cube, MySlimBlock block, bool b)
        {
            if (!SentisOptimisationsPlugin.Config.EnabledPcuLimiter || cube == null || block == null ||
                block.FatBlock == null)
                return;
            try
            {
                if (block.FatBlock.IsFunctional)
                {
                    if (IsLimitNotReached(cube))
                        return;
                    LimitReached(cube);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        public static void LimitReached(MyCubeGrid cube)
        {
            //Log.Error("Grid " + cube.DisplayName + " is over limit");
            gridsOverlimit.Add(cube.EntityId);
            List<IMySlimBlock> blocks = GridUtils.GetBlocks<IMyFunctionalBlock>((IMyCubeGrid)cube);
            foreach (var mySlimBlock in blocks)
            {
                if (noDisableBlock(mySlimBlock))
                {
                    continue;
                }

                ((IMyFunctionalBlock)mySlimBlock.FatBlock).Enabled = false;
            }

            var subGrids = GridUtils.GetSubGrids((IMyCubeGrid)cube);
            foreach (var myCubeGrid in subGrids)
            {
                gridsOverlimit.Add(myCubeGrid.EntityId);
                List<IMySlimBlock> subGridBlocks = GridUtils.GetBlocks<IMyFunctionalBlock>(myCubeGrid);
                foreach (var mySlimBlock in subGridBlocks)
                {
                    if (noDisableBlock(mySlimBlock))
                    {
                        continue;
                    }

                    ((IMyFunctionalBlock)mySlimBlock.FatBlock).Enabled = false;
                }
            }

            cube.RaiseGridChanged();
        }

        private static bool noDisableBlock(IMySlimBlock mySlimBlock)
        {
            return mySlimBlock.FatBlock is MyReactor ||
                   mySlimBlock.FatBlock is MySurvivalKit ||
                   mySlimBlock.FatBlock is MyJumpDrive ||
                   mySlimBlock.FatBlock is MyProjectorBase ||
                   mySlimBlock.FatBlock is MyShipConnector ||
                   mySlimBlock.FatBlock is MyMedicalRoom ||
                   mySlimBlock.FatBlock is MySafeZoneBlock;
        }

        public static void SendLimitMessage(long identityId, int pcu, int maxPcu, String gridName)
        {
            if (identityId == 0)
            {
                return;
            }

            ChatUtils.SendTo(identityId, "Для структуры " + gridName + " достигнут лимит PCU!");
            ChatUtils.SendTo(identityId, "Использовано " + pcu + " PCU из возможных " + maxPcu);
            MyVisualScriptLogicProvider.ShowNotification("Достигнут лимит PCU!", 10000, "Red",
                identityId);
            MyVisualScriptLogicProvider.ShowNotification("Использовано " + pcu + " PCU из возможных " + maxPcu, 10000,
                "Red",
                identityId);
        }

        private static bool IsLimitNotReached(MyCubeGrid cube)
        {
            var gridPcu = GridUtils.GetPCU((IMyCubeGrid)cube, true);
            var maxPcu = cube.IsStatic
                ? SentisOptimisationsPlugin.Config.MaxStaticGridPCU
                : SentisOptimisationsPlugin.Config.MaxDinamycGridPCU;
            var subGrids = GridUtils.GetSubGrids((IMyCubeGrid)cube);
            foreach (var myCubeGrid in subGrids)
            {
                if (myCubeGrid.IsStatic)
                {
                    maxPcu = SentisOptimisationsPlugin.Config.MaxStaticGridPCU;
                }
            }

            bool enemyAround = false;
            var owner = PlayerUtils.GetOwner((IMyCubeGrid)cube);
            foreach (var player in PlayerUtils.GetAllPlayers())
            {
                if (player.GetRelationTo(owner) != MyRelationsBetweenPlayerAndBlock.Enemies)
                {
                    continue;
                }

                var distance = Vector3D.Distance(player.GetPosition(), cube.PositionComp.GetPosition());
                if (distance > 15000)
                {
                    continue;
                }

                enemyAround = true;
            }

            var isLimitNotReached = gridPcu <= maxPcu;
            if (!isLimitNotReached)
            {
                SendLimitMessage(owner, gridPcu, maxPcu, cube.DisplayName);
            }

            maxPcu = enemyAround ? maxPcu : maxPcu + 5000;
            isLimitNotReached = gridPcu <= maxPcu;
            return isLimitNotReached;
        }
        
    }
}