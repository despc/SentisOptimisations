using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Havok;
using NLog;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRage.ModAPI;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class PerfomancePatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static Dictionary<long, long> entityesInSZ = new Dictionary<long, long>();
        public static Dictionary<long, MyWelder.ProjectionRaycastData[]> welderCache = new Dictionary<long, MyWelder.ProjectionRaycastData[]>();
        public static Dictionary<long, long> welderCounter = new Dictionary<long, long>();

        public static void Patch(PatchContext ctx)
        {
            var MethodPhantom_Leave = typeof(MySafeZone).GetMethod
                ("phantom_Leave", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodPhantom_Leave).Prefixes.Add(
                typeof(PerfomancePatch).GetMethod(nameof(MethodPhantom_LeavePatched),
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
            
            ctx.GetPattern(MethodFindProjectedBlocks).Suffixes.Add(
                typeof(PerfomancePatch).GetMethod(nameof(MethodFindProjectedBlocksPatchedAddCache),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            
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
                    if (welderCache.ContainsKey(__instance.EntityId))
                    {
                        __result = welderCache[__instance.EntityId];
                        welderCounter[__instance.EntityId] = welderCounter[__instance.EntityId] + 1; 
                        return false;
                    }
                }
            }
            welderCounter[__instance.EntityId] = 0; 
            return true;
        }
        
        private static void MethodFindProjectedBlocksPatchedAddCache(MyShipWelder __instance, ref MyWelder.ProjectionRaycastData[] __result)
        {
            welderCache[__instance.EntityId] = __result;
        }
        
        private static bool MethodUpdateThrustsPatched()
        {
            
            if (MySandboxGame.Static.SimulationFrameCounter % 3 == 0)
            {
                return true;
            }
            return false;
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