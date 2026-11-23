using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NLog;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// A grid taken out of the garage no longer takes the server down on its voxel check.
    ///
    /// The prod server fell on 23.11.2026 (13:10:47) with a NullReferenceException in
    /// <c>MyVoxelBase.IsAnyOfPointInside</c>, called from <c>MyGridStorageHelper.IsInsideVoxels</c> in the
    /// retrieval of a grid from the Services Terminal (<c>LoadGrid_Internal_Phase4</c>). <c>IsInsideVoxels</c> puts
    /// the static entities in the grid's box into a static list, <c>m_potentialPenetrations</c>, and clears it only
    /// when the grid is not in voxels: a grid that was in voxels leaves the planet's voxel physics there. A planet
    /// deletes those physics sectors when nothing is near them any more, and a deleted <c>MyVoxelPhysics</c> has no
    /// <c>Storage</c> (<c>BeforeDelete</c>). The next retrieval walks the stale entry and reads its storage.
    ///
    /// Fixed at both ends: the list is cleared before every check (the game fills it anew from the pruning structure,
    /// where there are only live entities), and a closed voxel or one without storage is "not inside" instead of
    /// dereferenced. Each stale or dead voxel found is written to the log.
    /// </summary>
    [PatchShim]
    public static class GarageVoxelCheck
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly Harmony Harmony = new Harmony("SentisOptimisations.GarageVoxelCheck");

        private static FieldInfo _penetrationsField;
        private static DateTime _deadLoggedAt = DateTime.MinValue;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("GarageVoxelCheck", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var helper = typeof(MyGridStorageHelper);
            _penetrationsField = helper.GetField("m_potentialPenetrations", BindingFlags.Static | BindingFlags.NonPublic)
                                 ?? throw new MissingFieldException("MyGridStorageHelper", "m_potentialPenetrations");
            if (!typeof(List<MyEntity>).IsAssignableFrom(_penetrationsField.FieldType))
                throw new InvalidOperationException("MyGridStorageHelper.m_potentialPenetrations is not what GarageVoxelCheck knows");
            var isInside = helper.GetMethod("IsInsideVoxels", instance)
                           ?? throw new MissingMethodException("MyGridStorageHelper", "IsInsideVoxels");
            var anyInside = typeof(MyVoxelBase).GetMethod(nameof(MyVoxelBase.IsAnyOfPointInside), instance, null, new[] { typeof(VRageMath.Vector3D[]) }, null)
                            ?? throw new MissingMethodException("MyVoxelBase", "IsAnyOfPointInside");

            ctx.GetPattern(isInside).Prefixes.Add(Own(nameof(IsInsidePrefix)));
            // By Harmony right away: the Torch prefix above is already registered, so a failure here must not be
            // reported by PatchGuard as "patches from this class are skipped" - the list is still cleared.
            try
            {
                Harmony.Patch(anyInside, prefix: new HarmonyMethod(Own(nameof(AnyInsidePrefix))));
            }
            catch (Exception e)
            {
                Log.Error(e, "Garage voxel check: the deleted-voxel guard is not patched; the stale list is still cleared");
            }
        }

        private static MethodInfo Own(string name) => typeof(GarageVoxelCheck).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        /// <summary>Whether a voxel cannot be read: closed, being closed or without storage.</summary>
        public static bool Dead(bool closed, bool markedForClose, bool hasStorage) => closed || markedForClose || !hasStorage;

        private static void IsInsidePrefix()
        {
            try
            {
                if (!(_penetrationsField.GetValue(null) is List<MyEntity> list) || list.Count == 0) return;
                var dead = 0;
                foreach (var e in list)
                    if (e == null || (e is MyVoxelBase v ? Dead(v.Closed, v.MarkedForClose, v.Storage != null) : e.Closed))
                        dead++;
                Log.Info($"Garage voxel check: {list.Count} entities left from an earlier check cleared ({dead} of them deleted)");
                list.Clear();
            }
            catch (Exception e)
            {
                Log.Error(e, "Garage voxel check: the stale list was not cleared");
            }
        }

        private static bool AnyInsidePrefix(MyVoxelBase __instance, ref bool __result)
        {
            if (!Dead(__instance.Closed, __instance.MarkedForClose, __instance.Storage != null)) return true;
            __result = false;
            var now = DateTime.UtcNow;
            if (now - _deadLoggedAt >= TimeSpan.FromMinutes(1))
            {
                _deadLoggedAt = now;
                Log.Warn($"Voxel check on a deleted voxel '{__instance.StorageName}' ({__instance.EntityId}, closed {__instance.Closed}, marked {__instance.MarkedForClose}, storage {__instance.Storage != null}): not inside\n{Environment.StackTrace}");
            }
            return false;
        }
    }
}
