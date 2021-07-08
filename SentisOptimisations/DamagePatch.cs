using System;
using System.Collections.Generic;
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
        public static Dictionary<long,long> contactInfo = new Dictionary<long, long>();
        public static void Patch(PatchContext ctx)
        {

            
            var MethodPerformDeformation = typeof(MyGridPhysics).GetMethod
                    ("PerformDeformation", BindingFlags.Instance | BindingFlags.NonPublic);
            
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
                if (separatingVelocity < 5)
                {
                    var myCubeGrid = (MyCubeGrid)__instance.Entity;
                    if (contactInfo.ContainsKey(myCubeGrid.EntityId))
                    {
                        contactInfo[myCubeGrid.EntityId] = contactInfo[myCubeGrid.EntityId] + 1;
                    }
                    else
                    {
                        contactInfo[myCubeGrid.EntityId] = 1;
                    }
                }
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
     
        internal static object GetInstanceField(Type type, object instance, string fieldName)
        {
            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Static;
            FieldInfo field = type.GetField(fieldName, bindFlags);
            return field.GetValue(instance);
        }
    }
}