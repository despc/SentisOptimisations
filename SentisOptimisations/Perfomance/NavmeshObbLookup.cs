using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.AI.Pathfinding.RecastDetour;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The navmesh tiles a path crosses, found without testing every tile the long way.
    ///
    /// <c>MyNavmeshOBBs.GetIntersectedOBB</c> is asked for every path an animal looks for (a wolf asks, twice a second,
    /// whether it can reach each player near it, and where to wander): it tests the line against every tile of its
    /// navmesh's grid - whether the tile holds either end, whether the line crosses it - each test turning the tile's
    /// orientation into a matrix. With the wolves about the bots on the stand that was 8 s in 10 minutes (dotTrace),
    /// most of the animals' 0.3 ms a frame.
    ///
    /// Here a tile the line stays further from than the tile's half-diagonal is left out first (the line can neither
    /// end in it nor cross it: every point of a tile is that near its centre), and the rest get the game's own tests.
    /// The same tiles, in the same order.
    /// </summary>
    [PatchShim]
    public static class NavmeshObbLookup
    {
        private static FieldInfo _obbs;
        private static Func<MyNavmeshOBBs, LineD, LineD> _toLocal;
        private static Func<MyNavmeshOBBs, MyOrientedBoundingBoxD, MyOrientedBoundingBoxD> _toWorld;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("NavmeshObbLookup", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(MyNavmeshOBBs);
            var method = type.GetMethod("GetIntersectedOBB", any, null, new[] { typeof(LineD) }, null)
                         ?? throw new MissingMethodException("MyNavmeshOBBs.GetIntersectedOBB");
            _obbs = type.GetField("m_obbs", any) ?? throw new MissingFieldException("MyNavmeshOBBs.m_obbs");
            var toLocal = type.GetMethod("ToLocal", any, null, new[] { typeof(LineD) }, null) ?? throw new MissingMethodException("MyNavmeshOBBs.ToLocal");
            var toWorld = type.GetMethod("ToWorld", any, null, new[] { typeof(MyOrientedBoundingBoxD) }, null) ?? throw new MissingMethodException("MyNavmeshOBBs.ToWorld");
            _toLocal = (Func<MyNavmeshOBBs, LineD, LineD>)Delegate.CreateDelegate(typeof(Func<MyNavmeshOBBs, LineD, LineD>), toLocal);
            _toWorld = (Func<MyNavmeshOBBs, MyOrientedBoundingBoxD, MyOrientedBoundingBoxD>)Delegate.CreateDelegate(
                typeof(Func<MyNavmeshOBBs, MyOrientedBoundingBoxD, MyOrientedBoundingBoxD>), toWorld);
            ctx.GetPattern(method).Prefixes.Add(typeof(NavmeshObbLookup).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>
        /// Whether a line may touch a box with this centre and half-extent: false only when the whole line is further
        /// from the centre than the box's half-diagonal.
        /// </summary>
        public static bool MayTouch(ref LineD line, ref Vector3D center, ref Vector3D halfExtent)
        {
            // first against the line's box grown by more than the half-diagonal (a few comparisons, most tiles out)
            double hx = Math.Abs(halfExtent.X), hy = Math.Abs(halfExtent.Y), hz = Math.Abs(halfExtent.Z);
            var grow = hx + hy + hz + 1e-6;
            double fx = line.From.X, fy = line.From.Y, fz = line.From.Z, tx = line.To.X, ty = line.To.Y, tz = line.To.Z;
            double cx = center.X, cy = center.Y, cz = center.Z;
            if (cx < Math.Min(fx, tx) - grow || cx > Math.Max(fx, tx) + grow ||
                cy < Math.Min(fy, ty) - grow || cy > Math.Max(fy, ty) + grow ||
                cz < Math.Min(fz, tz) - grow || cz > Math.Max(fz, tz) + grow) return false;
            // then the line's nearest point to the centre against the half-diagonal
            var reachSquared = (hx * hx + hy * hy + hz * hz) * (1 + 1e-9) + 1e-9;
            double ax = tx - fx, ay = ty - fy, az = tz - fz;
            var lengthSquared = ax * ax + ay * ay + az * az;
            var t = lengthSquared > 0 ? ((cx - fx) * ax + (cy - fy) * ay + (cz - fz) * az) / lengthSquared : 0;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            double dx = fx + ax * t - cx, dy = fy + ay * t - cy, dz = fz + az * t - cz;
            return dx * dx + dy * dy + dz * dz <= reachSquared;
        }

        private static bool Prefix(MyNavmeshOBBs __instance, LineD line, ref List<MyNavmeshOBBs.OBBCoords> __result)
        {
            if (!(_obbs.GetValue(__instance) is MyOrientedBoundingBoxD?[][] obbs) || obbs.Length == 0) return true;
            var local = _toLocal(__instance, line);
            var found = new List<(MyNavmeshOBBs.OBBCoords Tile, double Distance)>();
            var width = obbs[0].Length;
            for (var i = 0; i < obbs.Length; i++)
            for (var j = 0; j < width; j++)
            {
                // a missing tile: the game's own way (it throws there)
                if (!obbs[i][j].HasValue) return true;
                var obb = obbs[i][j].GetValueOrDefault();
                if (!MayTouch(ref local, ref obb.Center, ref obb.HalfExtent)) continue;
                if (obb.Contains(ref local.From) || obb.Contains(ref local.To) || obb.Intersects(ref local).HasValue)
                    found.Add((new MyNavmeshOBBs.OBBCoords { OBB = _toWorld(__instance, obb), Coords = new Vector2I(i, j) },
                        Vector3D.Distance(local.From, obb.Center)));
            }
            __result = found.OrderBy(f => f.Distance).Select(f => f.Tile).ToList();
            return false;
        }
    }
}
