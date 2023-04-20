using System;
using System.Collections.Generic;
using System.Reflection;
using NAPI;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Multiplayer;
using Torch.Managers.PatchManager;

namespace FixTurrets.Perfomance
{
    [PatchShim]
    public class ParallelUpdateTweaks
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static readonly Random r = new Random();
        private static Dictionary<long, int> ResourceDistributorCounters = new Dictionary<long, int>();

        private static Type MyThrusterBlockThrustComponentType =
            typeof(MyParallelEntityUpdateOrchestrator).Assembly.GetType(
                "Sandbox.Game.EntityComponents.MyThrusterBlockThrustComponent");

        private static MethodInfo GetEntityMethod = MyThrusterBlockThrustComponentType.GetProperty("Entity",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).GetMethod;

        public static void Patch(PatchContext ctx)
        {
            var MethodRenderUpdate = typeof(MyThrust).GetMethod
                ("RenderUpdate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            ctx.GetPattern(MethodRenderUpdate).Prefixes.Add(
                typeof(ParallelUpdateTweaks).GetMethod(nameof(Disabled),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodThrustUpdateBeforeSimulation = MyThrusterBlockThrustComponentType.GetMethod
                ("UpdateBeforeSimulation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            ctx.GetPattern(MethodThrustUpdateBeforeSimulation).Prefixes.Add(
                typeof(ParallelUpdateTweaks).GetMethod(nameof(MethodThrustUpdateBeforeSimulationPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodOnRegisteredToThrustComponent = typeof(MyThrust).GetMethod
                ("OnRegisteredToThrustComponent", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            ctx.GetPattern(MethodOnRegisteredToThrustComponent).Prefixes.Add(
                typeof(ParallelUpdateTweaks).GetMethod(nameof(OnRegisteredToThrustComponentPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
                
            var MethodSoundEmitterUpdate = typeof(MyEntity3DSoundEmitter).GetMethod
                (nameof(MyEntity3DSoundEmitter.Update), BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodSoundEmitterUpdate).Prefixes.Add(
                typeof(ParallelUpdateTweaks).GetMethod(nameof(Disabled),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodUpdateHeadAndWeapon = typeof(MyCharacter).GetMethod
                ("UpdateHeadAndWeapon", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodUpdateHeadAndWeapon).Prefixes.Add(
                typeof(ParallelUpdateTweaks).GetMethod(nameof(Disabled),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));


            var MethodSendDirtyBlockLimit = typeof(MyPlayerCollection).GetMethod
                (nameof(MyPlayerCollection.SendDirtyBlockLimit), BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodSendDirtyBlockLimit).Prefixes.Add(
                typeof(ParallelUpdateTweaks).GetMethod(nameof(Slowdown10),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        
        private static bool OnRegisteredToThrustComponentPatched(MyThrust __instance, ref bool __result)
        {
            try
            {
                MyResourceSinkComponent resourceSinkComponent = ((MyEntityThrustComponent)__instance.easyGetField("m_thrustComponent")).ResourceSink(__instance);
                resourceSinkComponent.IsPoweredChanged += new Action(__instance.Sink_IsPoweredChanged);
                resourceSinkComponent.Update();
                __result = true;
            }
            catch (Exception e)
            {
                Log.Error("Register thrust exception ", e);
            }
           
            return false;
        }
        
        private static bool MethodThrustUpdateBeforeSimulationPatched(Object __instance)
        {
            MyCubeGrid grid = (MyCubeGrid)GetEntityMethod.Invoke(__instance, new object[] { });

            if (grid == null || grid.IsStatic)
            {
                return false;
            }

            return true;
        }

        private static bool Disabled()
        {
            return false;
        }

        private static bool Slowdown10()
        {
            if (r.NextDouble() < 0.05)
            {
                return true;
            }

            return false;
        }
    }
}