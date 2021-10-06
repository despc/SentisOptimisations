using System;
using System.Collections.Generic;
using System.Reflection;
using Havok;
using NLog;
using ParallelTasks;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class DamagePatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static Dictionary<long,long> contactInfo = new Dictionary<long, long>();
        private static bool _init;
        
        public static void Init()
        {
            if (_init)
                return;
            _init = true;
            //MyAPIGateway.Session.DamageSystem.RegisterBeforeDamageHandler(0, ProcessDamage);
        }    
        
        private static void ProcessDamage(object target, ref MyDamageInformation info)
        {
            try
            {
                DoProcessDamage(target, ref info);
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private static void DoProcessDamage(object target, ref MyDamageInformation info)
        {
            
            if (info.Type != MyDamageType.Deformation)
            {
                return;
            }
            
            IMySlimBlock damagedBlock = target as IMySlimBlock;
            
            if (damagedBlock == null)
            {
                return;
            }
            
            if (damagedBlock.FatBlock != null)
            {
                return;
            }

            if (damagedBlock.BlockDefinition.Id.SubtypeName.Contains("Titanium"))
            {
                info.Amount = info.Amount / 20;
            }
            
            if (damagedBlock.BlockDefinition.Id.SubtypeName.Contains("Aluminum"))
            {
                info.Amount = info.Amount / 5;
            }
        }

        public static void Patch(PatchContext ctx)
        {

            
            var MethodPerformDeformation = typeof(MyGridPhysics).GetMethod
                    ("PerformDeformation", BindingFlags.Instance | BindingFlags.NonPublic);
            
            ctx.GetPattern(MethodPerformDeformation).Prefixes.Add(
                typeof(DamagePatch).GetMethod(nameof(PatchPerformDeformation),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            
            var MethodSpinOnce = typeof(MySpinWait).GetMethod
                (nameof(MySpinWait.SpinOnce), BindingFlags.Instance | BindingFlags.Public);
            
            ctx.GetPattern(MethodSpinOnce).Prefixes.Add(
                typeof(DamagePatch).GetMethod(nameof(SpinOnce),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            
        }


        
        private static bool SpinOnce()
        {
            return false;
        }
        private static bool PatchPerformDeformation(
            MyGridPhysics __instance,
            ref HkBreakOffPointInfo pt,
            bool fromBreakParts,
            float separatingVelocity,
            MyEntity otherEntity)
        {

            if (otherEntity is MyVoxelBase && separatingVelocity < 30)
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
            
            // if (otherEntity is MyCubeGrid)
            // {
            //     if (((MyCubeGrid) otherEntity).Mass < 500000)
            //     {
            //         return false;
            //     }
            // }
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