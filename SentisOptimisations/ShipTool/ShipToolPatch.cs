using System;
using System.Reflection;
using NLog;
using Sandbox.Definitions;
using Sandbox.Game.Weapons;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisOptimisationsPlugin.ShipTool
{
    [PatchShim]
    public static class ShipToolPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static void Patch(PatchContext ctx)
        {
            var MethodLoadDummies = typeof(MyShipToolBase).GetMethod(
                "LoadDummies", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodLoadDummies).Suffixes.Add(
                typeof(ShipToolPatch).GetMethod(nameof(LoadDummiesPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodMyShipDrillInit = typeof(MyShipDrill).GetMethod(
                nameof(MyShipDrill.Init), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

            ctx.GetPattern(MethodMyShipDrillInit).Suffixes.Add(
                typeof(ShipToolPatch).GetMethod(nameof(MyShipDrillInitPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static void LoadDummiesPatch(MyShipToolBase __instance)
        {
            try
            {
                BoundingSphere boundingSphere = (BoundingSphere) ReflectionUtils.GetInstanceField(typeof(MyShipToolBase), __instance, "m_detectorSphere");
                boundingSphere.Radius = boundingSphere.Radius * 2;
                ReflectionUtils.SetInstanceField(typeof(MyShipToolBase), __instance, "m_detectorSphere", boundingSphere);
            }
            catch (Exception e)
            {
                Log.Error("Exception in during LoadDummiesPatch", e);
            }
        }
        
        private static void MyShipDrillInitPatch(MyShipDrill __instance)
        {
            try
            {
                MyDrillBase drillBase = (MyDrillBase) ReflectionUtils.GetInstanceField(typeof(MyShipDrill), __instance, "m_drillBase");
                var myDrillCutOut = new MyDrillCutOut(((MyShipDrillDefinition) __instance.BlockDefinition).CutOutOffset,
                    ((MyShipDrillDefinition) __instance.BlockDefinition).CutOutRadius * 2);
                ReflectionUtils.SetInstanceField(typeof(MyDrillBase), drillBase, "m_cutOut", myDrillCutOut);
            }
            catch (Exception e)
            {
                Log.Error("Exception in during MyShipDrillInitPatch", e);
            }
        }
    }
}