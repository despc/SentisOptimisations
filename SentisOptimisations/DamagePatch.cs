using System;
using System.Reflection;
using Havok;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class DamagePatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {

            // var MethodPerformDeformationOnGroup = typeof(MyGridPhysics).GetMethod
            //     ("PerformDeformationOnGroup", BindingFlags.Instance | BindingFlags.NonPublic);
            
            var MethodPerformDeformation = typeof(MyGridPhysics).GetMethod
                    ("PerformDeformation", BindingFlags.Instance | BindingFlags.NonPublic);

            // ctx.GetPattern(MethodPerformDeformationOnGroup).Prefixes.Add(
            //     typeof(DamagePatch).GetMethod(nameof(PatchPerformDeformationOnGroup),
            //         BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            ctx.GetPattern(MethodPerformDeformation).Prefixes.Add(
                typeof(DamagePatch).GetMethod(nameof(PatchPerformDeformation),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }


        private static bool PatchPerformDeformation(
            MyGridPhysics __instance,
            ref HkBreakOffPointInfo pt,
            bool fromBreakParts,
            float separatingVelocity,
            MyEntity otherEntity)
        {

            if (otherEntity is MyVoxelBase && separatingVelocity < 40)
            {
                return false;
            }
            
            if (otherEntity is MyCubeGrid)
            {
                if (((MyCubeGrid) otherEntity).Mass < 500000)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool PatchPerformDeformationOnGroup(
            MyGridPhysics __instance,
            MyEntity entity,
            MyEntity other,
            ref HkBreakOffPointInfo pt,
            float separatingVelocity)
        {
            try
            {
                MyCubeGrid cubeGrid = (MyCubeGrid) GetInstanceField(__instance.GetType(), __instance, "m_grid");
                if (cubeGrid.IsStatic)
                {
                    return true;
                }
                
                if (cubeGrid.Mass < 500000)
                {
                    return true;
                }

                if (cubeGrid == entity)
                {
                    if (other is MyCubeGrid)
                    {
                        if (((MyCubeGrid)other).IsStatic)
                        {
                            return true;
                        }
                        if (((MyCubeGrid)other).Mass < 500000)
                        {
                            return false;
                        }
                    }
                }
                
                if (cubeGrid == other)
                {
                    if (entity is MyCubeGrid)
                    {
                        if (((MyCubeGrid)entity).IsStatic)
                        {
                            return true;
                        }
                        if (((MyCubeGrid)entity).Mass < 500000)
                        {
                            return false;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("Exception in time PatchPerformDeformationOnGroup", e);
            }
            return true;
        }
        
        internal static object GetInstanceField(Type type, object instance, string fieldName)
        {
            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Static;
            FieldInfo field = type.GetField(fieldName, bindFlags);
            return field.GetValue(instance);
        }
    }
}