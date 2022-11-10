using System;
using System.Collections.Generic;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Definitions.SessionComponents;
using VRageMath;

namespace Scripts.Shared {
    public static class Voxels {
        
        public static bool IsInsideVoxel(this IMySlimBlock block, MyGridPlacementSettings settings) { 
            if (block.FatBlock == null) return false; //ERROR
            var def = block.BlockDefinition;
            if (!(def is MyCubeBlockDefinition)) return false;
            
            var CurrentBlockDefinition = def as MyCubeBlockDefinition;
            var cellSize = MyDefinitionManager.Static.GetCubeSize(CurrentBlockDefinition.CubeSize);
            var localBB = new BoundingBoxD(-CurrentBlockDefinition.Size * cellSize * 0.5f, CurrentBlockDefinition.Size * cellSize * 0.5f);
            var isAllowed = MyCubeGrid.IsAabbInsideVoxel(block.FatBlock.WorldMatrix, localBB, settings);
            return !isAllowed;
        }

        public static bool IsInsideVoxels(this IMyPlayer Me_Player) {
            try {
                var Me = Me_Player.Character;
                if (Me == null) return false; 
                var vsettings = new VoxelPlacementSettings {PlacementMode = VoxelPlacementMode.Volumetric, MaxAllowed = 0.95f, MinAllowed = 0};
                var settings = new MyGridPlacementSettings {
                   CanAnchorToStaticGrid = true,
                   EnablePreciseRotationWhenSnapped = true,
                   SearchHalfExtentsDeltaAbsolute = 0,
                   SearchHalfExtentsDeltaRatio = 0,
                   SnapMode = SnapMode.Base6Directions,
                   VoxelPlacement = vsettings
                };

                var worldMatrix = Me.WorldMatrix;
                var localAabb = Me.LocalAABB;

                return MyCubeGrid.IsAabbInsideVoxel(worldMatrix, localAabb, settings);
            } catch (Exception e) {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "[Player_SOS_Rescue]");
                return false;
            }
        }

        public static bool IsGridInsideVoxel(IMyCubeGrid cubeGrid) { 
            try {
                MyGridPlacementSettings grid_settings = new MyGridPlacementSettings();
                MyGridPlacementSettings settings = new MyGridPlacementSettings();

                settings.CanAnchorToStaticGrid = true;
                settings.EnablePreciseRotationWhenSnapped = true;
                settings.SearchHalfExtentsDeltaAbsolute = 0;
                settings.SearchHalfExtentsDeltaRatio = 0;
                settings.SnapMode = SnapMode.Base6Directions;

                grid_settings = settings;//WTF HERE??

                var vsettings = new VoxelPlacementSettings();
                vsettings.PlacementMode = VoxelPlacementMode.Volumetric;
                vsettings.MaxAllowed = 0.95f;
                vsettings.MinAllowed = 0;

                var grid_vsettings = new VoxelPlacementSettings();
                grid_vsettings.PlacementMode = VoxelPlacementMode.Volumetric;
                grid_vsettings.MaxAllowed = 0.20f;
                grid_vsettings.MinAllowed = 0;

                grid_settings.VoxelPlacement = grid_vsettings;
                settings.VoxelPlacement = vsettings;

                MatrixD grid_worldMatrix = cubeGrid.WorldMatrix;
                BoundingBoxD cubeGrid_localAABB = cubeGrid.LocalAABB;

                return MyCubeGrid.IsAabbInsideVoxel(grid_worldMatrix, cubeGrid_localAABB, grid_settings);
            } catch (Exception e) {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "[Player_SOS_Rescue] ");
                //return;
            }
            return false;
        }


        public static bool CheckEachGridBlock(IMyCubeGrid cubeGrid, MyGridPlacementSettings settings)  { 
            var blocks = new List<IMySlimBlock>();
            cubeGrid.GetBlocks(blocks, block => block.FatBlock != null);
            foreach (IMySlimBlock block in blocks) {
                if (!block.IsInsideVoxel(settings)) continue;
                var Block_Type = block.BlockDefinition.Id.SubtypeName;
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error("Grid in Voxels " + Block_Type);
                return true;
            }

            return false;
        }
    }
}