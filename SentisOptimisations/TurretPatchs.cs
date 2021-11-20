using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using Torch.Managers.PatchManager;
using Torch.Utils;
using VRage.Game;
using VRage.Game.Components;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class FixTurretsPatch
    {
        [ReflectedMethod(Name = "RotateModels")]
        private static Action<MyLargeTurretBase> RotateModels;

        private static Dictionary<long, int> _checkTargetsSlowdown = new Dictionary<long, int>();

        private static void OnPositionChanged(MyPositionComponentBase myPositionComponentBase,
            MyLargeTurretBase __instance)
        {
            if ((ulong) __instance.EntityId % 100 == MySandboxGame.Static.SimulationFrameCounter % 100)
            {
                RotateModels.Invoke(__instance);
            }
        }

        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            Log.Info("Patch init");
            var methodInfos = typeof(MyLargeTurretBase).GetMethods(BindingFlags.Instance | BindingFlags.Public);
            var initMethodInfo = methodInfos.Where(m => m.Name.Equals("Init")).ToArray()[0];
            ctx.GetPattern(initMethodInfo).Prefixes.Add(
                typeof(FixTurretsPatch).GetMethod(nameof(TurretInitPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));


            var methodInfo = typeof(MyLargeTurretBase).GetMethod("CheckAndSelectNearTargetsParallel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            
            ctx.GetPattern(methodInfo).Prefixes.Add(
                typeof(FixTurretsPatch).GetMethod(nameof(DisableCheckAndSelectNearTargetsParallel),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }


        private static void TurretInitPatch(MyLargeTurretBase __instance, MyObjectBuilder_CubeBlock objectBuilder,
            MyCubeGrid cubeGrid)
        {
            __instance.PositionComp.OnPositionChanged += pos =>
                OnPositionChanged(pos, __instance);
        }

        private static bool DisableCheckAndSelectNearTargetsParallel(MyLargeTurretBase __instance)
        {
            int count;
            var instanceEntityId = __instance.EntityId;
            if (_checkTargetsSlowdown.TryGetValue(instanceEntityId, out count))
            {
                if (count > SentisOptimisationsPlugin.Config.CheckAndSelectNearTargetsSlowdown)
                {
                    return true;
                }

                _checkTargetsSlowdown[instanceEntityId] = count + 1;
                return false;
            }
            _checkTargetsSlowdown[instanceEntityId] = 1;
            return false;
        }
    }
}