using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using NAPI;
using NLog;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Weapons;
using Sandbox.Game.Weapons.Guns;
using Sandbox.Game.World;
using Sandbox.Game.WorldEnvironment;
using Sandbox.Game.WorldEnvironment.Modules;
using Sandbox.ModAPI;
using SentisGameplayImprovements.AllGridsActions;
using SentisOptimisations;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using Torch.Utils;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace SentisOptimisationsPlugin.ShipTool
{
    [PatchShim]
    public static class ShipToolPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        
        [ReflectedGetter(Name = "m_detectorSphere")]
        private static Func<MyShipToolBase, BoundingSphere> _detectorSphere;
        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("ShipToolPatch", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {

            var DrillEnvironmentSector = typeof(MyDrillBase).GetMethod(
                "DrillEnvironmentSector", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(DrillEnvironmentSector).Prefixes.Add(
                typeof(ShipToolPatch).GetMethod(nameof(DrillEnvironmentSectorPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool DrillEnvironmentSectorPatch(MyDrillSensorBase.DetectionInfo entry,
            float speedMultiplier,
            out MyStringHash targetMaterial,
            MyDrillBase __instance,
            ref bool __result)
        {
            targetMaterial = MyStringHash.GetOrCompute("Wood");
            try
            {
                __instance.GetType().GetProperty("DrilledEntity").SetMethod.Invoke(__instance, new[] { entry.Entity });
                __instance.GetType().GetProperty("DrilledEntityPoint").SetMethod.Invoke(__instance, new object[] { entry.DetectionPoint });
                //__instance.DrilledEntity = entry.Entity;
                //__instance.DrilledEntityPoint = entry.DetectionPoint;
                if (Sync.IsServer)
                {
                    if ((int)__instance.easyGetField("m_lastItemId") != entry.ItemId)
                    {
                        __instance.easySetField("m_lastItemId", entry.ItemId);
                        __instance.easySetField("m_lastContactTime", MySandboxGame.TotalGamePlayTimeInMilliseconds);
                    }
                    if ((double) (MySandboxGame.TotalGamePlayTimeInMilliseconds - (int)__instance.easyGetField("m_lastContactTime")) > 1500.0 * (double) speedMultiplier)
                    {
                        MyBreakableEnvironmentProxy module = (entry.Entity as MyEnvironmentSector).GetModule<MyBreakableEnvironmentProxy>();
                        var drillEntity = ((MyEntity)__instance.easyGetField("m_drillEntity"));
                        Vector3D vector3D = drillEntity.WorldMatrix.Forward + drillEntity.WorldMatrix.Right;
                        vector3D.Normalize();
                        int itemId = entry.ItemId;
                        Vector3D detectionPoint = entry.DetectionPoint;
                        Vector3D hitnormal = vector3D;
                        module.BreakAt(itemId, detectionPoint, hitnormal);
                        __instance.easySetField("m_lastContactTime", MySandboxGame.TotalGamePlayTimeInMilliseconds);
                        __instance.easySetField("m_lastItemId", 0);
                    }
                }
                __result = true;
                
            }
            catch (Exception e)
            {
                //Log.Error("Exception during DrillEnvironmentSector", e);
            }
        
            return false;
        }

        public static float GetWelderRadius(MyShipWelder welder)
        {
            // Respect the live work-radius multiplier: read the current detector sphere radius
            // (kept in sync by ShipToolRadiusPatch / the runtime Ship tools settings) instead of
            // the raw definition, which would ignore the multiplier on the projection-weld path.
            var sphere = _detectorSphere != null ? _detectorSphere.Invoke(welder) : default;
            if (sphere.Radius > 0f)
                return sphere.Radius;
            return ((MyShipWelderDefinition)(welder.BlockDefinition)).SensorRadius;
        }

    }
}