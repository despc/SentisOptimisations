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
    /// retrieval of a grid from the Services Terminal (<c>LoadGrid_Internal_Phase4</c>, the completion of a parallel
    /// task: the game thread). <c>IsInsideVoxels</c> adds the static entities in the grid's box to a static list,
    /// <c>m_potentialPenetrations</c> (the pruning query appends, it does not clear), and clears it only when the grid
    /// is not in voxels: a grid that was in voxels leaves the planet's voxel physics there. A planet deletes those
    /// physics sectors when nothing is near them any more, and a deleted <c>MyVoxelPhysics</c> has no <c>Storage</c>
    /// (<c>BeforeDelete</c>). The next retrieval walks the stale entry and reads its storage.
    ///
    /// The list is cleared after every check, both ways it ends (so it holds no entity between checks), and before one
    /// as well, should a check have ended in an exception: the game fills it anew from the pruning structure, where there
    /// are only live entities. And a voxel without storage is "not inside" instead of dereferenced: on that alone, not on
    /// a voxel being closed - <c>IsAnyOfPointInside</c> is called by everyone (the plugins too), and a voxel marked for
    /// close keeps its storage and is read as the game reads it.
    /// </summary>
    [PatchShim]
    public static class GarageVoxelCheck
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly Harmony Harmony = new Harmony("SentisOptimisations.GarageVoxelCheck");

        private static FieldInfo _penetrationsField;
        private static List<MyEntity> _penetrations;
        private static DateTime _deadLoggedAt = DateTime.MinValue;

        /// <summary>Entities found left in the list before a check (an earlier one ended in an exception), since the start.</summary>
        public static int StaleCleared;
        /// <summary>Voxels without storage answered "not inside" instead of read, since the start.</summary>
        public static int DeadSkipped;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("GarageVoxelCheck", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var helper = typeof(MyGridStorageHelper);
            _penetrationsField = helper.GetField("m_potentialPenetrations", BindingFlags.Static | BindingFlags.NonPublic)
                                 ?? throw new MissingFieldException("MyGridStorageHelper", "m_potentialPenetrations");
            _penetrations = _penetrationsField.GetValue(null) as List<MyEntity>
                            ?? throw new InvalidOperationException("MyGridStorageHelper.m_potentialPenetrations is not what GarageVoxelCheck knows");
            var isInside = helper.GetMethod("IsInsideVoxels", instance)
                           ?? throw new MissingMethodException("MyGridStorageHelper", "IsInsideVoxels");
            var anyInside = typeof(MyVoxelBase).GetMethod(nameof(MyVoxelBase.IsAnyOfPointInside), instance, null, new[] { typeof(VRageMath.Vector3D[]) }, null)
                            ?? throw new MissingMethodException("MyVoxelBase", "IsAnyOfPointInside");

            var pattern = ctx.GetPattern(isInside);
            pattern.Prefixes.Add(Own(nameof(IsInsidePrefix)));
            pattern.Suffixes.Add(Own(nameof(IsInsideSuffix)));
            // By Harmony right away: the Torch patches above are already registered, so a failure here must not be
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

        /// <summary>Whether a voxel cannot be read: it has no storage (a deleted voxel physics sector).</summary>
        public static bool Unreadable(bool hasStorage) => !hasStorage;

        private static void IsInsidePrefix()
        {
            try
            {
                if (_penetrations == null || _penetrations.Count == 0) return;
                var count = _penetrations.Count;
                StaleCleared += count;
                _penetrations.Clear();
                if (global::SentisOptimisations.DiagLog.On)
                    Log.Info($"Garage voxel check: {count} entities left from an earlier check cleared");
            }
            catch (Exception e)
            {
                Log.Error(e, "Garage voxel check: the stale list was not cleared");
            }
        }

        private static void IsInsideSuffix()
        {
            try
            {
                _penetrations?.Clear();
            }
            catch (Exception e)
            {
                Log.Error(e, "Garage voxel check: the list was not cleared after a check");
            }
        }

        private static bool AnyInsidePrefix(MyVoxelBase __instance, ref bool __result)
        {
            if (!Unreadable(__instance.Storage != null)) return true;
            __result = false;
            System.Threading.Interlocked.Increment(ref DeadSkipped);   // (the plugins call it on their workers too)
            var now = DateTime.UtcNow;
            if (now - _deadLoggedAt >= TimeSpan.FromMinutes(1))
            {
                _deadLoggedAt = now;
                Log.Warn($"Voxel check on a voxel without storage '{__instance.StorageName}' ({__instance.EntityId}, closed {__instance.Closed}, marked {__instance.MarkedForClose}): not inside\n{Environment.StackTrace}");
            }
            return false;
        }
    }
}
