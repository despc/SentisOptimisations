using System;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using VRage.Network;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// A procedural asteroid a player comes near is given to the player's client before the player drills it.
    ///
    /// A client makes the procedural asteroids round it itself, from the seeds, and the server does not replicate an
    /// asteroid nobody has changed (<c>MyVoxelReplicable.ShouldReplicate</c>): the client's copy is its own. The
    /// first cut of a drill makes the asteroid changed, and only then is it replicated to the client - streamed, the
    /// client's copy replaced - and until it is there the drilling does not take on the client. Players saw it as
    /// "the drill does nothing, then the asteroid streams and it drills" (10.10.2026).
    ///
    /// Here an unchanged asteroid is replicated to a client whose player is within <see cref="NearM"/> of it. An
    /// unchanged asteroid is sent by its id alone - no voxel data - and the client takes its own copy for it (the
    /// game's own path for such an asteroid), so it costs next to nothing; when the player drills, the client has
    /// the server's asteroid already. Once given, it stays until it leaves the client's replication range, as any
    /// replicable; the client keeps its own copy when it is taken back (only a saved asteroid is closed then).
    ///
    /// The other half: an asteroid's box for the replication layers is inflated by the view distance's
    /// <c>sqrt(ViewDistance²/3) - SyncDistance</c>, which is negative when the sync distance is not smaller than the
    /// view distance (10 km and 10 km on the server, 15 and 15 on the stand): the box turned inside out, the near
    /// layers (20 m - 5 km, refreshed every 1 - 16 s) never met it, and a drilled asteroid reached the client only
    /// with the outermost layer, refreshed every 32 s - 37 s after the first cut on the stand. The box is never made
    /// smaller than the asteroid itself.
    /// </summary>
    [PatchShim]
    public static class AsteroidNearReplication
    {
        /// <summary>How near a player has to be to an asteroid for the player's client to be given it unchanged.</summary>
        public const double NearM = 500;

        private static readonly Harmony Harmony = new Harmony("SentisOptimisations.AsteroidNearReplication");
        private static Func<object, MyVoxelBase> _voxel;

        // Read by SentisTests.
        public static long GivenNear;
        public static long BoxesKept;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("AsteroidNearReplication", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var type = typeof(MyVoxelBase).Assembly.GetType("Sandbox.Game.Replication.MyVoxelReplicable")
                       ?? throw new TypeLoadException("Sandbox.Game.Replication.MyVoxelReplicable");
            var should = type.GetMethod("ShouldReplicate", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(MyClientInfo) }, null)
                         ?? throw new MissingMethodException("MyVoxelReplicable.ShouldReplicate");
            var aabb = type.GetMethod("GetAABB", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)
                       ?? throw new MissingMethodException("MyVoxelReplicable.GetAABB");
            var instance = type.GetProperty("Instance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)
                           ?? throw new MissingMemberException("MyVoxelReplicable.Instance");
            var replicable = Expression.Parameter(typeof(object));
            _voxel = Expression.Lambda<Func<object, MyVoxelBase>>(
                Expression.Convert(Expression.Property(Expression.Convert(replicable, type), instance), typeof(MyVoxelBase)), replicable).Compile();

            // by Harmony, not Torch: a Torch suffix with a ref __result of a struct (GetAABB's BoundingBoxD) threw
            // NullReferenceException in the patched method itself, and the stand did not load the world (10.10.2026)
            HarmonyMethod Own(string name) => new HarmonyMethod(typeof(AsteroidNearReplication).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
            Harmony.Patch(should, postfix: Own(nameof(ShouldReplicateSuffix)));
            Harmony.Patch(aabb, postfix: Own(nameof(GetAABBSuffix)));
        }

        private static void ShouldReplicateSuffix(object __instance, MyClientInfo client, ref bool __result)
        {
            if (__result) return;
            try
            {
                var voxel = _voxel(__instance);
                if (voxel == null || voxel is MyPlanet || voxel.Closed || voxel.Storage == null || !client.IsValid) return;
                var at = client.State?.Position;
                if (!at.HasValue) return;
                if (!Near(voxel.PositionComp.WorldAABB, at.Value)) return;
                __result = true;
                System.Threading.Interlocked.Increment(ref GivenNear);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "AsteroidNearReplication.ShouldReplicate");
            }
        }

        private static void GetAABBSuffix(object __instance, ref BoundingBoxD __result)
        {
            try
            {
                var voxel = _voxel(__instance);
                if (voxel == null || voxel is MyPlanet) return;
                var own = voxel.PositionComp.WorldAABB;
                if (Covers(__result, own)) return;
                __result = own;
                System.Threading.Interlocked.Increment(ref BoxesKept);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "AsteroidNearReplication.GetAABB");
            }
        }

        /// <summary>Whether a point is within <see cref="NearM"/> of a box.</summary>
        public static bool Near(BoundingBoxD box, Vector3D at) => box.Distance(at) <= NearM;

        /// <summary>Whether a box (as the game inflated it) still holds the asteroid's own one.</summary>
        public static bool Covers(BoundingBoxD box, BoundingBoxD own) =>
            box.Min.X <= own.Min.X && box.Min.Y <= own.Min.Y && box.Min.Z <= own.Min.Z &&
            box.Max.X >= own.Max.X && box.Max.Y >= own.Max.Y && box.Max.Z >= own.Max.Z;
    }
}
