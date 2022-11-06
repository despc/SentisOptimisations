using System;
using System.Collections.Generic;
using System.Reflection;
using NAPI;
using NLog;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using Torch.Utils;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class WheelPatch
    {

        public static HashSet<long> gridsWithEngine = new HashSet<long>();
            
            
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        [ReflectedMethodInfo(typeof (MyMotorSuspension), "UpdatePropulsion")]
        internal static readonly MethodInfo original;
        
        [ReflectedMethodInfo(typeof (WheelPatch), "UpdatePropulsionPatched")]
        private static readonly MethodInfo patched;
        public static void Patch(PatchContext ctx)
        {
            ctx.GetPattern(original).Prefixes.Add(patched);
        }

        private static bool UpdatePropulsionPatched(MyMotorSuspension __instance, bool forward, bool backwards)
        {
            try
            {
                if (ShouldBrake(__instance))
                    return false;
                bool flag = false;
                float propulsionOverride = __instance.PropulsionOverride;
                var engineExist = gridsWithEngine.Contains(__instance.CubeGrid.EntityId);
                float multiplier = engineExist ? SentisOptimisationsPlugin.Config.EngineMultiplier : 1;
                if ((double)propulsionOverride != 0.0)
                {
                    bool forward1 = (double)propulsionOverride > 0.0;
                    if (__instance.InvertPropulsion)
                        forward1 = !forward1;
                    flag = true;
                    __instance.easyCallMethod("Accelerate", new object[]{Math.Abs(propulsionOverride) * __instance.BlockDefinition.PropulsionForce * multiplier, forward1});
                }
                else if (forward)
                {
                    flag = true;
                    __instance.easyCallMethod("Accelerate", new object[]{__instance.BlockDefinition.PropulsionForce * multiplier * __instance.Power, !__instance.InvertPropulsion});
                }
                else if (backwards)
                {
                    flag = true;
                    __instance.easyCallMethod("Accelerate", new object[]{__instance.BlockDefinition.PropulsionForce * multiplier * __instance.Power, __instance.InvertPropulsion});
                }

                SetInternalFrictionEnabled(__instance, !flag);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
            return false;
        }

        private static bool ShouldBrake(MyMotorSuspension __instance)
        {

                if (!__instance.BrakingEnabled)
                    return false;
                if (__instance.Brake)
                    return true;
                return __instance.Handbrake && __instance.IsParkingEnabled;
            
        }
        
        private static void SetInternalFrictionEnabled(MyMotorSuspension __instance, bool value)
        {
            if (!((bool)__instance.easyGetField("m_defaultInternalFriction") || (bool) __instance.easyGetField("m_internalFrictionEnabled") == value))
                return;
            __instance.easySetField("m_internalFrictionEnabled", value);
            __instance.easyCallMethod("ResetConstraintFriction", new object[]{});
        }

    }
}