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
        public static Dictionary<long, int> Cooldowns = new Dictionary<long, int>();
        public static Dictionary<long, int> NobodyToOff = new Dictionary<long, int>();
        public static readonly Random r = new Random();
        
        [ReflectedGetter(Name = "m_detectorSphere")]
        private static Func<MyShipToolBase, BoundingSphere> _detectorSphere;
        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("ShipToolPatch", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {

            var MethodActivateCommon = typeof(MyShipToolBase).GetMethod(
                "ActivateCommon", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodActivateCommon).Prefixes.Add(
                typeof(ShipToolPatch).GetMethod(nameof(ActivateCommonPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

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

        /// <summary>
        /// Throttles tool activations on grids no player is near; an activation that is not skipped
        /// is the game's own ActivateCommon.
        ///
        /// This used to replace ActivateCommon with a copy from the time the scan ran on worker
        /// threads. On the game thread the copy only lost to the original: it found entities by
        /// enumerating the entity observer's cache, whose enumerator copied every grid, character and voxel
        /// map of the world through ConcurrentDictionary.Keys on each call, allocated new collections
        /// for every activation, reached the tool's fields and methods through reflection with boxing,
        /// and dropped the game's cache of the tool's own grid blocks. With 220 idle welders that was
        /// 401 MB of garbage a minute, 45% of everything the game thread allocated. The game's version
        /// queries MyGamePruningStructure into a shared list and allocates next to nothing.
        /// </summary>
        private static bool ActivateCommonPatch(MyShipToolBase __instance)
        {
            try
            {
                if (!SentisOptimisationsPlugin.Config.SlowdownEnabled ||
                    MySandboxGame.Static.SimulationFrameCounter <= 6000)
                    return true;

                var blockId = __instance.EntityId;
                var myUpdateTiersPlayerPresence = __instance.CubeGrid.PlayerPresenceTier;
                if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier1)
                    return !NeedSkip(blockId, 30);
                if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier2)
                {
                    int nobodyToOffCount = 0;
                    if (NobodyToOff.TryGetValue(blockId, out nobodyToOffCount))
                    {
                        NobodyToOff[blockId] = nobodyToOffCount++;
                        if (nobodyToOffCount > 5000)
                        {
                            __instance.Enabled = false;
                            NobodyToOff.Remove(blockId);
                            return false;
                        }
                    }
                    else
                    {
                        NobodyToOff[blockId] = 0;
                    }

                    return !NeedSkip(blockId, 300);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "ship tool throttling failed");
            }

            return true;
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

        private static bool NeedSkip(long blockId, int cd)
        {
            int cooldown;
            if (Cooldowns.TryGetValue(blockId, out cooldown))
            {
                if (cooldown > cd)
                {
                    Cooldowns[blockId] = 0;
                    return false;
                }
                Cooldowns[blockId] = cooldown + 1;
                return true;
            }

            Cooldowns[blockId] = r.Next(0, cd);
            return true;
        }
    }
}