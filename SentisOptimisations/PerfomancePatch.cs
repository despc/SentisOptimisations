using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Havok;
using NLog;
using ParallelTasks;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using Torch.Managers.PatchManager;
using VRage.ModAPI;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class PerfomancePatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static Dictionary<long, long> entityesInSZ = new Dictionary<long, long>();

        public static void Patch(PatchContext ctx)
        {
            var MethodPhantom_Leave = typeof(MySafeZone).GetMethod
                ("phantom_Leave", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodPhantom_Leave).Prefixes.Add(
                typeof(PerfomancePatch).GetMethod(nameof(MethodPhantom_LeavePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            
            var MethodIsTargetInSz = typeof(MyLargeTurretBase).GetMethod
                (nameof(MyLargeTurretBase.IsTargetInSafeZone), BindingFlags.Instance | BindingFlags.Public);
           
            ctx.GetPattern(MethodIsTargetInSz).Prefixes.Add(
                typeof(PerfomancePatch).GetMethod(nameof(MethodIsTargetInSzPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic)); 
            
            
            var MethodInitializeWorkerArrays = typeof(PrioritizedScheduler).GetMethod
                ("InitializeWorkerArrays", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodInitializeWorkerArrays).Prefixes.Add(
                typeof(PerfomancePatch).GetMethod(nameof(InitializeWorkerArraysPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static void InitializeWorkerArraysPatched(ref int threadCount, ref bool amd)
        {
            amd = false;
            threadCount = 10;
        }

        private static bool MethodIsTargetInSzPatched(ref bool __result)
        {
            __result = false;
            return false;
        }


        private static bool MethodPhantom_LeavePatched(MySafeZone __instance, HkPhantomCallbackShape sender,
            HkRigidBody body)
        {
            try
            {
                IMyEntity entity = body.GetEntity(0U);
                if (entity == null)
                    return false;
                var stopwatch = Stopwatch.StartNew();
                InvokeInstanceMethod(__instance.GetType(), __instance, "RemoveEntityPhantom",
                    new object[] {body, entity});
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
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn("MethodPhantom_LeavePatched Exception ", e);
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