using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Havok;
using NLog;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Weapons;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRage.ModAPI;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class PerfomancePatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static Dictionary<long, long> entityesInSZ = new Dictionary<long, long>();
            //public static Dictionary<long, MyWelder.ProjectionRaycastData[]> welderCache = new Dictionary<long, MyWelder.ProjectionRaycastData[]>();
        public static Dictionary<long, long> welderCounter = new Dictionary<long, long>();
        public static Dictionary<long, long> thrustCounter = new Dictionary<long, long>();

        public static void Patch(PatchContext ctx)
        {
            var MethodPhantom_Leave = typeof(MySafeZone).GetMethod
                ("phantom_Leave", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodPhantom_Leave).Prefixes.Add(
                typeof(PerfomancePatch).GetMethod(nameof(MethodPhantom_LeavePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            
            var MethodThrustDamageAsync = typeof(MyThrust).GetMethod
                ("ThrustDamageAsync", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodThrustDamageAsync).Prefixes.Add(
                typeof(PerfomancePatch).GetMethod(nameof(MethodThrustDamageAsyncPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            // Кажется это уменьшает тягу)
           // var assembly = typeof(MySafeZone).Assembly;
           // var MethodUpdateThrusts = assembly.GetType("Sandbox.Game.EntityComponents.MyThrusterBlockThrustComponent").GetMethod
           //     ("UpdateThrusts", BindingFlags.Instance | BindingFlags.NonPublic);
           // 
           // ctx.GetPattern(MethodUpdateThrusts).Prefixes.Add(
           //     typeof(PerfomancePatch).GetMethod(nameof(MethodUpdateThrustsPatched),
           //         BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodFindProjectedBlocks = typeof(MyShipWelder).GetMethod
                ("FindProjectedBlocks", BindingFlags.Instance | BindingFlags.NonPublic);
            
            ctx.GetPattern(MethodFindProjectedBlocks).Prefixes.Add(
                typeof(PerfomancePatch).GetMethod(nameof(MethodFindProjectedBlocksPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            //ctx.GetPattern(MethodFindProjectedBlocks).Suffixes.Add(
            //    typeof(PerfomancePatch).GetMethod(nameof(MethodFindProjectedBlocksPatchedAddCache),
            //        BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            
            
            //var MethodBuildInternal = typeof(MyProjectorBase).GetMethod
            //    ("BuildInternal", BindingFlags.Instance | BindingFlags.NonPublic);
            //
            //ctx.GetPattern(MethodBuildInternal).Prefixes.Add(
            //    typeof(PerfomancePatch).GetMethod(nameof(MethodBuildInternalPatched),
            //        BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            //var MethodMyReplicationServerDestroy = typeof(MyReplicationServer).GetMethod
            //    (nameof(MyReplicationServer.Destroy), BindingFlags.Instance | BindingFlags.Public);
            //
            //ctx.GetPattern(MethodMyReplicationServerDestroy).Prefixes.Add(
            //    typeof(PerfomancePatch).GetMethod(nameof(MethodMyReplicationServerDestroyPatched),
            //        BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool MethodPhantom_LeavePatched(MySafeZone __instance, HkPhantomCallbackShape sender,
            HkRigidBody body)
        {
            IMyEntity entity = body.GetEntity(0U);
            if (entity == null)
                return false;
            var stopwatch = Stopwatch.StartNew();
            InvokeInstanceMethod(__instance.GetType(), __instance, "RemoveEntityPhantom", new object[] {body, entity});
            stopwatch.Stop();
            var stopwatchElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
            if (stopwatchElapsedMilliseconds < 4)
            {
                return false;
            }

            if (entityesInSZ.ContainsKey(entity.EntityId))
            {
                entityesInSZ[entity.EntityId] = entityesInSZ[entity.EntityId] + stopwatchElapsedMilliseconds;
            }
            else
            {
                entityesInSZ[entity.EntityId] = stopwatchElapsedMilliseconds;
            }

            return false;
        }

        //private static bool MethodMyReplicationServerDestroyPatched(MyReplicationServer __instance, IMyReplicable obj)
        //{
//
        //    if (obj is MyExternalReplicable)
        //    {
        //        if (obj is MyExternalReplicable<MyCubeGrid>)
        //        {
        //            Log.Error("Удаляем грид " + ((MyExternalReplicable<MyCubeGrid>) obj).Instance.DisplayName);
        //        }
        //        if (obj is MyExternalReplicable<MySyncedBlock>)
        //        {
        //            Log.Error("Удаляем блок " + ((MyExternalReplicable<MySyncedBlock>) obj).Instance.DisplayName);
        //        }
        //        var instanceName = ((MyExternalReplicable) obj).InstanceName;
        //    }
        //    return true;
        //}
        
        private static bool MethodFindProjectedBlocksPatched(MyShipWelder __instance, ref MyWelder.ProjectionRaycastData[] __result)
        {
            
            if (welderCounter.ContainsKey(__instance.EntityId))
            {
                if (welderCounter[__instance.EntityId] < 5)
                {
                    //if (welderCache.ContainsKey(__instance.EntityId))
                    //{
                        __result = new MyWelder.ProjectionRaycastData[0];
                        welderCounter[__instance.EntityId] = welderCounter[__instance.EntityId] + 1; 
                    //    return false;
                    //}
                }
            }
            welderCounter[__instance.EntityId] = 0; 
            return true;
        }


        private static bool MethodBuildInternalPatched(MyProjectorBase __instance,
            Vector3I cubeBlockPosition,
            long owner,
            long builder,
            bool requestInstant = true,
            long builtBy = 0)
        {
            try
            {
                InvokeInstanceMethod(typeof(MyProjectorBase), __instance, "BuildInternal",
                    new object[] {cubeBlockPosition, owner, builder, requestInstant, builtBy});
            }
            catch (Exception e)
            {
                //welderCache.Clear();
                welderCounter.Clear();
                Log.Error(e);
            }
            return false;
    }
        
        private static void MethodFindProjectedBlocksPatchedAddCache(MyShipWelder __instance,
            ref MyWelder.ProjectionRaycastData[] __result)
        {
            //welderCache[__instance.EntityId] = __result;
        }

        private static bool MethodThrustDamageAsyncPatched(MyThrust __instance)
        {
            if (thrustCounter.ContainsKey(__instance.EntityId))
            {
                if (thrustCounter[__instance.EntityId] < 5)
                {
                    thrustCounter[__instance.EntityId] = thrustCounter[__instance.EntityId] + 1;
                    return false;
                }
            }

            thrustCounter[__instance.EntityId] = 0;
            return true;
        }

        private static object InvokeInstanceMethod(Type type, object instance, string methodName, Object[] args)
        {
            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Static;
            var method = type.GetMethod(methodName, bindFlags);
            return method.Invoke(instance, args);
        }
    }
}