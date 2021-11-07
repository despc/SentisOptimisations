using System;
using System.Reflection;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using SentisOptimisations;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public class MergePatch
    {
        public static void Patch(PatchContext ctx)
        {
                // ctx.GetPattern(typeof(MyShipMergeBlock).GetMethod("CreateConstraint",
                //         BindingFlags.Instance | BindingFlags.NonPublic))
                //     .Prefixes.Add(typeof(MergePatch).GetMethod("CreateConstraintPatched",
                //         BindingFlags.Static | BindingFlags.NonPublic));  
        }


        
        
        private static bool CreateConstraintPatched(MyShipMergeBlock __instance, MyCubeGrid other, MyShipMergeBlock block)
        {
            try
            {
                HkPrismaticConstraintData data = new HkPrismaticConstraintData();
                Vector3 posA = (Vector3) ReflectionUtils.InvokeInstanceMethod(typeof(MyShipMergeBlock), __instance,
                    "ConstraintPositionInGridSpace", new object[] { });
                Vector3 posB = (Vector3) ReflectionUtils.InvokeInstanceMethod(typeof(MyShipMergeBlock), block,
                    "ConstraintPositionInGridSpace", new object[] { });
                // Vector3 posA = __instance.ConstraintPositionInGridSpace();
                // Vector3 posB = block.ConstraintPositionInGridSpace();
                var m_forward = (Base6Directions.Direction) ReflectionUtils.GetInstanceField(typeof(MyShipMergeBlock), __instance,
                    "m_forward");
                Vector3 directionVector1 = __instance.PositionComp.LocalMatrixRef.GetDirectionVector(m_forward);
                var m_right = (Base6Directions.Direction) ReflectionUtils.GetInstanceField(typeof(MyShipMergeBlock), __instance,
                    "m_right");
                var block_m_right = (Base6Directions.Direction) ReflectionUtils.GetInstanceField(typeof(MyShipMergeBlock), block,
                    "m_right");
                Vector3 directionVector2 = __instance.PositionComp.LocalMatrixRef.GetDirectionVector(m_right);
                Vector3 axisB = -block.PositionComp.LocalMatrixRef.GetDirectionVector(m_forward);
                Base6Directions.Direction closestDirection1 = block.WorldMatrix.GetClosestDirection(__instance.WorldMatrix.GetDirectionVector(m_right));
                Base6Directions.Direction closestDirection2 = __instance.WorldMatrix.GetClosestDirection(block.WorldMatrix.GetDirectionVector(block_m_right));
                Vector3 directionVector3 = block.PositionComp.LocalMatrixRef.GetDirectionVector(closestDirection1);
                data.SetInBodySpace(posA, posB, directionVector1, axisB, directionVector2, directionVector3, (MyPhysicsBody) __instance.CubeGrid.Physics, (MyPhysicsBody) other.Physics);
                HkMalleableConstraintData malleableConstraintData = new HkMalleableConstraintData();
                malleableConstraintData.SetData((HkConstraintData) data);
                data.ClearHandle();
                malleableConstraintData.Strength = 0.0000001f;
                malleableConstraintData.MaximumAngularImpulse = (float) 0;
                malleableConstraintData.MaximumLinearImpulse = (float) 0;
                HkConstraint hkConstraint = new HkConstraint(__instance.CubeGrid.Physics.RigidBody, other.Physics.RigidBody, (HkConstraintData) malleableConstraintData);
                ReflectionUtils.InvokeInstanceMethod(typeof(MyShipMergeBlock), __instance,
                    "AddConstraint", new object[1] {hkConstraint});
                ReflectionUtils.InvokeInstanceMethod(typeof(MyShipMergeBlock), __instance,
                    "SetConstraint", new object[3] {block, hkConstraint, closestDirection2});
                MyShipMergeBlock otherMerge = (MyShipMergeBlock) ReflectionUtils.GetInstanceField(typeof(MyShipMergeBlock), __instance,
                    "m_other");
                ReflectionUtils.InvokeInstanceMethod(typeof(MyShipMergeBlock),  otherMerge,
                    "SetConstraint", new object[3] {__instance, hkConstraint, closestDirection1});
                // __instance.AddConstraint(hkConstraint);
                // __instance.SetConstraint(block, hkConstraint, closestDirection2);
                // __instance.m_other.SetConstraint(__instance, hkConstraint, closestDirection1);
                return false;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn("CreateConstraintPatched Exception ", e);
            }
            return true;
        }
    }
    

}