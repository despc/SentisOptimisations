using System;
using System.Reflection;
using NLog;
using Sandbox.Game.Weapons;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisOptimisationsPlugin.ShipTool
{
    [PatchShim]
    public class ShipToolPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static void Patch(PatchContext ctx)
        {
            var MethodLoadDummies = typeof(MyShipToolBase).GetMethod(
                "LoadDummies", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodLoadDummies).Suffixes.Add(
                typeof(ShipToolPatch).GetMethod(nameof(LoadDummiesPatch),
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
    }
}