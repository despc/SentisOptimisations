using System;
using System.Linq;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using Torch.Managers.PatchManager;
using Torch.Utils;
using VRage.Game;
using VRage.Game.Components;

namespace FixTurrets
{
    [PatchShim]
    public static class FixTurretsPatch
    {
        
        [ReflectedMethod(Name = "RotateModels")]
        private static Action<MyLargeTurretBase> RotateModels;
        


        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            Log.Info("Patch init");

            var methodInfos = typeof(MyLargeTurretBase).GetMethods(BindingFlags.Instance | BindingFlags.Public);
            var initMethodInfo = methodInfos.Where(m => m.Name.Equals("Init")).ToArray()[0];
            ctx.GetPattern(initMethodInfo).Prefixes.Add(
                typeof(FixTurretsPatch).GetMethod(nameof(TurretInitPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }
        
        
        private static void TurretInitPatch(MyLargeTurretBase __instance, MyObjectBuilder_CubeBlock objectBuilder, MyCubeGrid cubeGrid)
        {
            __instance.PositionComp.OnPositionChanged += pos =>
                OnPositionChanged(pos, __instance);
        }
        
        private static void OnPositionChanged(MyPositionComponentBase myPositionComponentBase, MyLargeTurretBase __instance)
        {
            RotateModels.Invoke(__instance);
        }
    }
}