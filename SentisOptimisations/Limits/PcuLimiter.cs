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
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin
{
    public class PcuLimiter
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        HashSet<long> gridsOverlimit = new HashSet<long>();
        public void OnLoaded()
        {
            MyCubeGrids.BlockBuilt += MyCubeGrids_BlockBuilt;
            MyCubeGrids.BlockFunctional += MyCubeGrids_BlockFunctional;
            MyCubeGrids.BlockDestroyed += MyCubeGridsOnBlockDestroyed;
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
            List<IMySlimBlock> blocks = GridUtils.GetBlocks<IMyFunctionalBlock>(cube);
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

                ((IMyFunctionalBlock) mySlimBlock.FatBlock).EnabledChanged -= OnEnabledChanged();
                ((IMyFunctionalBlock) mySlimBlock.FatBlock).Enabled = true;
            }
            
                        
            var subGrids = GridUtils.GetSubGrids(cube);
            foreach (var myCubeGrid in subGrids)
            {
                if (gridsOverlimit.Contains(myCubeGrid.EntityId))
                {
                    gridsOverlimit.Remove(myCubeGrid.EntityId);
                }
                List<IMySlimBlock> subGridBlocks = GridUtils.GetBlocks<IMyFunctionalBlock>(myCubeGrid);
                foreach (var mySlimBlock in subGridBlocks)
                {
                    if (mySlimBlock.FatBlock is MyReactor ||
                        mySlimBlock.FatBlock is MySurvivalKit ||
                        mySlimBlock.FatBlock is MyMedicalRoom ||
                        mySlimBlock.FatBlock is MyProjectorBase ||
                        mySlimBlock.FatBlock is MyShipConnector ||
                        mySlimBlock.FatBlock is MySafeZoneBlock)
                    {
                        continue;
                    }

                    ((IMyFunctionalBlock) mySlimBlock.FatBlock).EnabledChanged -= OnEnabledChanged();
                    ((IMyFunctionalBlock) mySlimBlock.FatBlock).Enabled = true;
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

        public void LimitReached(MyCubeGrid cube)
        {
            Log.Error("Grid " + cube.DisplayName + " is over limit");
            gridsOverlimit.Add(cube.EntityId);
            List<IMySlimBlock> blocks = GridUtils.GetBlocks<IMyFunctionalBlock>(cube);
            foreach (var mySlimBlock in blocks)
            {
                if (mySlimBlock.FatBlock is MyReactor ||
                    mySlimBlock.FatBlock is MySurvivalKit ||
                    mySlimBlock.FatBlock is MyProjectorBase ||
                    mySlimBlock.FatBlock is MyShipConnector ||
                    mySlimBlock.FatBlock is MyMedicalRoom ||
                    mySlimBlock.FatBlock is MySafeZoneBlock)
                {
                    continue;
                }

                ((IMyFunctionalBlock) mySlimBlock.FatBlock).Enabled = false;
                ((IMyFunctionalBlock) mySlimBlock.FatBlock).EnabledChanged += OnEnabledChanged();
            }
            
            var subGrids = GridUtils.GetSubGrids(cube);
            foreach (var myCubeGrid in subGrids)
            {
                gridsOverlimit.Add(myCubeGrid.EntityId);
                List<IMySlimBlock> subGridBlocks = GridUtils.GetBlocks<IMyFunctionalBlock>(myCubeGrid);
                foreach (var mySlimBlock in subGridBlocks)
                {
                    if (mySlimBlock.FatBlock is MyReactor ||
                        mySlimBlock.FatBlock is MySurvivalKit ||
                        mySlimBlock.FatBlock is MyProjectorBase ||
                        mySlimBlock.FatBlock is MyShipConnector ||
                        mySlimBlock.FatBlock is MyMedicalRoom ||
                        mySlimBlock.FatBlock is MySafeZoneBlock)
                    {
                        continue;
                    }

                    ((IMyFunctionalBlock) mySlimBlock.FatBlock).Enabled = false;
                    ((IMyFunctionalBlock) mySlimBlock.FatBlock).EnabledChanged += OnEnabledChanged();
                }
            }

            cube.RaiseGridChanged();
        }

        private static Action<IMyTerminalBlock> OnEnabledChanged()
        {
            return terminalBlock =>
            {
                if (!IsLimitNotReached(((MyFunctionalBlock) terminalBlock).CubeGrid))
                {
                    ((MyFunctionalBlock) terminalBlock).Enabled = false;
                }
            };
        }

        public static void SendLimitMessage(long identityId, int pcu, int maxPcu)
        {
            ChatUtils.SendTo(identityId, "Достигнут лимит PCU!");
            ChatUtils.SendTo(identityId, "Использовано " + pcu + " PCU из возможных " + maxPcu);
            MyVisualScriptLogicProvider.ShowNotification("Достигнут лимит PCU!", 10000, "Red",
                identityId);
            MyVisualScriptLogicProvider.ShowNotification("Использовано " + pcu + " PCU из возможных " + maxPcu, 10000, "Red",
                identityId);
        }
        private static bool IsLimitNotReached(MyCubeGrid cube)
        {
            var gridPcu = GridUtils.GetPCU(cube, true);
            var maxPcu = cube.IsStatic
                ? SentisOptimisationsPlugin.Config.MaxStaticGridPCU
                : SentisOptimisationsPlugin.Config.MaxDinamycGridPCU;
            var subGrids = GridUtils.GetSubGrids(cube);
            foreach (var myCubeGrid in subGrids)
            {
                if (myCubeGrid.IsStatic)
                {
                    maxPcu = SentisOptimisationsPlugin.Config.MaxStaticGridPCU;
                }
            }
            var isLimitNotReached = gridPcu <= maxPcu;
            if (!isLimitNotReached)
            {
                var owner = PlayerUtils.GetOwner(cube);
                SendLimitMessage(owner, gridPcu, maxPcu);
            }
            return isLimitNotReached;
        }

        public void OnUnloading()
        {
            MyCubeGrids.BlockBuilt -= MyCubeGrids_BlockBuilt;
            MyCubeGrids.BlockDestroyed -= MyCubeGridsOnBlockDestroyed;
        }
    }
}