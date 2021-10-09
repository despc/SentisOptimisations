using System;
using System.Reflection;
using Havok;
using NLog;
using ParallelTasks;
using Sandbox.Engine.Physics;
using Sandbox.Game.Weapons;
using SentisOptimisations;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class PerfomancePatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static void Patch(PatchContext ctx)
        {
            var MyPhysicsLoadData = typeof(MyPhysics).GetMethod
                (nameof(MyPhysics.LoadData), BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MyPhysicsLoadData).Suffixes.Add(
                typeof(PerfomancePatch).GetMethod(nameof(MyPhysicsLoadDataPatched),
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

        private static void MyPhysicsLoadDataPatched()
        {
            try
            {
                ReflectionUtils.SetPrivateStaticField(typeof(MyPhysics), "m_threadPool", new HkJobThreadPool(11));
                ReflectionUtils.SetPrivateStaticField(typeof(MyPhysics), "m_jobQueue", new HkJobQueue(12));
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn("MySafeZoneUpdateBeforeSimulationPatched Exception ", e);
            }
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