using System;
using System.Collections.Generic;
using System.Reflection;
using Havok;
using NAPI;
using NLog;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using Torch.Managers.PatchManager;
using VRage.Game.ModAPI.Ingame;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class VoxelProtectorPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static HashSet<IMyUpgradeModule> Protectors = null;

        public static void Patch(PatchContext ctx)
        {
            var MethodMakeCraterInternal = typeof(MyVoxelGenerator).GetMethod(
                "MakeCraterInternal",
                BindingFlags.Static | BindingFlags.NonPublic);

            ctx.GetPattern(MethodMakeCraterInternal).Prefixes.Add(
                typeof(VoxelProtectorPatch).GetMethod(nameof(PatchMakeCraterInternal),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodCutOutShapeWithProperties = typeof(MyVoxelGenerator).GetMethod(
                "CutOutShapeWithProperties",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            ctx.GetPattern(MethodCutOutShapeWithProperties).Prefixes.Add(
                typeof(VoxelProtectorPatch).GetMethod(nameof(PatchCutOutShape),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodCutOutShapeWithPropertiesAsync = typeof(MyVoxelBase).GetMethod(
                "CutOutShapeWithPropertiesAsync",
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            ctx.GetPattern(MethodCutOutShapeWithPropertiesAsync).Prefixes.Add(
                typeof(VoxelProtectorPatch).GetMethod(nameof(PatchCutOutShape),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var type = typeof(MyVoxelBase).Assembly.GetType("Sandbox.Game.MyExplosion");

            var MethodCCutOutVoxelMap = type.GetMethod(
                "CutOutVoxelMap",
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            
            ctx.GetPattern(MethodCCutOutVoxelMap).Prefixes.Add(
                typeof(VoxelProtectorPatch).GetMethod(nameof(PatchCutOutVoxelMap),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            
            var MethodCutOutShape = typeof(MyVoxelGenerator).GetMethod(
                "CutOutShape",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            ctx.GetPattern(MethodCutOutShape).Prefixes.Add(
                typeof(VoxelProtectorPatch).GetMethod(nameof(PatchCutOutShape),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodBreakLogicHandler = typeof(MyGridPhysics).GetMethod(
                "BreakLogicHandler",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            ctx.GetPattern(MethodBreakLogicHandler).Prefixes.Add(
                typeof(VoxelProtectorPatch).GetMethod(nameof(PatchBreakLogicHandler),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodRequestCutOut = typeof(MyShipMiningSystem).GetMethod(
                "RequestCutOut",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            ctx.GetPattern(MethodRequestCutOut).Prefixes.Add(
                typeof(VoxelProtectorPatch).GetMethod(nameof(PatchRequestCutOut),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool PatchBreakLogicHandler(HkRigidBody otherBody, MyGridPhysics __instance,
            ref HkBreakOffLogicResult __result)
        {
            try
            {
                if (Protectors == null)
                {
                    return true;
                }

                var pos = ((MyCubeGrid)__instance.easyGetField("m_grid")).PositionComp.GetPosition();

                foreach (var myUpgradeModule in Protectors)
                {
                    if (Vector3D.Distance(myUpgradeModule.PositionComp.GetPosition(), pos) < 300)
                    {
                        if (myUpgradeModule.Enabled && myUpgradeModule.IsFunctional)
                        {
                            IMyEntity entity1 = otherBody.GetEntity(0U);
                            if (entity1 is MyVoxelBase)
                            {
                                __result = HkBreakOffLogicResult.DoNotBreakOff;
                                return false;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }

            return true;
        }

        private static bool PatchMakeCraterInternal(BoundingSphereD sphere)
        {
            try
            {
                var pos = sphere.Center;
                if (Protectors == null)
                {
                    return true;
                }

                foreach (var myUpgradeModule in Protectors)
                {
                    if (Vector3D.Distance(myUpgradeModule.PositionComp.GetPosition(), pos) < 300)
                    {
                        if (myUpgradeModule.Enabled && myUpgradeModule.IsFunctional)
                        {
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }

            return true;
        }
        
        private static bool PatchRequestCutOut(Vector3D hitPosition)
        {
            try
            {
                var pos = hitPosition;
                if (Protectors == null)
                {
                    return true;
                }

                foreach (var myUpgradeModule in Protectors)
                {
                    if (Vector3D.Distance(myUpgradeModule.PositionComp.GetPosition(), pos) < 300)
                    {
                        if (myUpgradeModule.Enabled && myUpgradeModule.IsFunctional)
                        {
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }

            return true;
        }
        

        private static bool PatchCutOutVoxelMap(Vector3D center)
        {
            try
            {
                var pos = center;
                if (Protectors == null)
                {
                    return true;
                }

                foreach (var myUpgradeModule in Protectors)
                {
                    if (Vector3D.Distance(myUpgradeModule.PositionComp.GetPosition(), pos) < 300)
                    {
                        if (myUpgradeModule.Enabled && myUpgradeModule.IsFunctional)
                        {
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }

            return true;
        }
        
        private static bool PatchCutOutShape(MyShape shape)
        {
            try
            {
                var pos = shape.GetWorldBoundaries().Center;
                if (Protectors == null)
                {
                    return true;
                }

                foreach (var myUpgradeModule in Protectors)
                {
                    if (Vector3D.Distance(myUpgradeModule.PositionComp.GetPosition(), pos) < 300)
                    {
                        if (myUpgradeModule.Enabled && myUpgradeModule.IsFunctional)
                        {
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }

            return true;
        }
    }
}