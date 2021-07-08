using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Havok;
using NLog;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using VRage.ModAPI;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class PerfomanceProfilePatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static Dictionary<long, long> entityesInSZ = new Dictionary<long, long>();

        public static void Patch(PatchContext ctx)
        {
            var MethodPhantom_Leave = typeof(MySafeZone).GetMethod
                ("phantom_Leave", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodPhantom_Leave).Prefixes.Add(
                typeof(PerfomanceProfilePatch).GetMethod(nameof(MethodPhantom_LeavePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
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

        private static object InvokeInstanceMethod(Type type, object instance, string methodName, Object[] args)
        {
            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Static;
            var method = type.GetMethod(methodName, bindFlags);
            return method.Invoke(instance, args);
        }
    }
}