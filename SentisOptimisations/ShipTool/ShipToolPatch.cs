using System;
using System.Reflection;
using NLog;
using Sandbox.Definitions;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SentisOptimisations;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisOptimisationsPlugin.ShipTool
{
    [PatchShim]
    public static class ShipToolPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        
        public static Random r = new Random();

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

            var MethodActivateCommon = typeof(MyShipToolBase).GetMethod(
                "ActivateCommon", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodActivateCommon).Prefixes.Add(
                typeof(ShipToolPatch).GetMethod(nameof(ActivateCommonPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static void LoadDummiesPatch(MyShipToolBase __instance)
        {
            try
            {
                BoundingSphere boundingSphere =
                    (BoundingSphere) ReflectionUtils.GetInstanceField(typeof(MyShipToolBase), __instance,
                        "m_detectorSphere");
                boundingSphere.Radius = boundingSphere.Radius *
                                        SentisOptimisationsPlugin.Config.ShipGrinderWelderRadiusMultiplier;
                ReflectionUtils.SetInstanceField(typeof(MyShipToolBase), __instance, "m_detectorSphere",
                    boundingSphere);
            }
            catch (Exception e)
            {
                Log.Error("Exception in during LoadDummiesPatch", e);
            }
        }

        private static void ActivateCommonPatch(MyShipToolBase __instance)
        {
            if (!(__instance is MyShipWelder))
            {
                return;
            }

            if (!__instance.CubeGrid.IsStatic)
            {
                SetRadius(__instance, (float) (r.NextDouble() * SentisOptimisationsPlugin.Config.ShipWelderRadius));
                return;
            }

            var myShipWelder = ((MyShipWelder) __instance);
            var ownerId = myShipWelder.OwnerId;
            var playerFaction = MySession.Static.Factions.GetPlayerFaction(ownerId);
            if (playerFaction == null)
            {
                SetRadius(__instance, (float) (r.NextDouble() * SentisOptimisationsPlugin.Config.ShipWelderRadius));
                return;
            }

            if (!myShipWelder.CustomData.Contains("[AZ_REWARD]"))
            {
                SetRadius(__instance, (float) (r.NextDouble() * SentisOptimisationsPlugin.Config.ShipWelderRadius));
                return;
            }

            var factionTag = playerFaction.Tag;

            if (!SentisOptimisationsPlugin.Config.AzWinners.Contains(factionTag))
            {
                SetRadius(__instance, (float) (r.NextDouble() * SentisOptimisationsPlugin.Config.ShipWelderRadius));
                return;
            }

            SetRadius(__instance, (float) (r.NextDouble() * SentisOptimisationsPlugin.Config.ShipSuperWelderRadius));
        }

        private static void SetRadius(MyShipToolBase __instance, float radius)
        {
            BoundingSphere m_detectorSphere =
                (BoundingSphere) ReflectionUtils.GetInstanceField(typeof(MyShipToolBase), __instance,
                    "m_detectorSphere");
            BoundingSphere bs = new BoundingSphere(m_detectorSphere.Center, radius);
            ReflectionUtils.SetInstanceField(typeof(MyShipToolBase), __instance, "m_detectorSphere", bs);
        }

        private static void MyShipDrillInitPatch(MyShipDrill __instance)
        {
            try
            {
                MyDrillBase drillBase =
                    (MyDrillBase) ReflectionUtils.GetInstanceField(typeof(MyShipDrill), __instance, "m_drillBase");
                var myDrillCutOut = new MyDrillCutOut(((MyShipDrillDefinition) __instance.BlockDefinition).CutOutOffset,
                    ((MyShipDrillDefinition) __instance.BlockDefinition).CutOutRadius *
                    SentisOptimisationsPlugin.Config.ShipDrillRadiusMultiplier);
                ReflectionUtils.SetInstanceField(typeof(MyDrillBase), drillBase, "m_cutOut", myDrillCutOut);
            }
            catch (Exception e)
            {
                Log.Error("Exception in during MyShipDrillInitPatch", e);
            }
        }
    }
}