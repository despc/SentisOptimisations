using System;
using System.Collections.Concurrent;
using System.Reflection;
using Havok;
using NLog;
using ParallelTasks;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.Game.Weapons;
using SentisOptimisations;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class PerfomancePatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static ConcurrentDictionary<Key, bool> conveyourCache = new ConcurrentDictionary<Key, bool>();

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


            //Кэш конвеера
            var MethodComputeCanTransfer = typeof(MyGridConveyorSystem).GetMethod
                (nameof(MyGridConveyorSystem.ComputeCanTransfer), BindingFlags.Static | BindingFlags.Public);

            ctx.GetPattern(MethodComputeCanTransfer).Prefixes.Add(
                typeof(PerfomancePatch).GetMethod(nameof(ComputeCanTransferPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodComputeCanTransferPost = typeof(MyGridConveyorSystem).GetMethod
                (nameof(MyGridConveyorSystem.ComputeCanTransfer), BindingFlags.Static | BindingFlags.Public);

            ctx.GetPattern(MethodComputeCanTransferPost).Suffixes.Add(
                typeof(PerfomancePatch).GetMethod(nameof(ComputeCanTransferPatchedPost),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static void InitializeWorkerArraysPatched(ref int threadCount, ref bool amd)
        {
            amd = false;
            threadCount = 10;
        }

        private static bool ComputeCanTransferPatched(IMyConveyorEndpointBlock start,
            IMyConveyorEndpointBlock end, ref bool __result)
        {
            try
            {
                if (start is MyCubeBlock && end is MyCubeBlock)
                {
                    var startEntityId = ((MyCubeBlock) start).EntityId;
                    var endEntityId = ((MyCubeBlock) end).EntityId;
                    var key = new Key(startEntityId, endEntityId);
                    if (conveyourCache.ContainsKey(key))
                    {
                        __result = conveyourCache[key];
                        return false;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }

            return true;
        }

        private static void ComputeCanTransferPatchedPost(IMyConveyorEndpointBlock start,
            IMyConveyorEndpointBlock end, bool __result)
        {
            try
            {
                if (start is MyCubeBlock && end is MyCubeBlock)
                {
                    var startEntityId = ((MyCubeBlock) start).EntityId;
                    var endEntityId = ((MyCubeBlock) end).EntityId;
                    var key = new Key(startEntityId, endEntityId);
                    var hashCode = key.GetHashCode();
                    conveyourCache[key] = __result;
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
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

        public struct Key
        {
            public readonly long Part1;
            public readonly long Part2;

            public Key(long p1, long p2)
            {
                Part1 = p1;
                Part2 = p2;
            }

            public override bool Equals(object obj)
            {
                if (obj is Key)
                {
                    return Part1.Equals(((Key) obj).Part1) && Part2.Equals(((Key) obj).Part2);
                }

                return false;
            }

            public override int GetHashCode()
            {
                return Part1.GetHashCode() + Part2.GetHashCode();
            }
        }
    }
}