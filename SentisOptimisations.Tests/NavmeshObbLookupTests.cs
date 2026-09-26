using System;
using SentisOptimisationsPlugin;
using VRageMath;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class NavmeshObbLookupTests
    {
        private static bool GameSays(MyOrientedBoundingBoxD obb, LineD line) =>
            obb.Contains(ref line.From) || obb.Contains(ref line.To) || obb.Intersects(ref line).HasValue;

        [Fact]
        public void A_line_far_from_the_box_is_left_out()
        {
            var line = new LineD(new Vector3D(100, 0, 0), new Vector3D(200, 0, 0));
            var center = Vector3D.Zero;
            var half = new Vector3D(5, 5, 5);
            Assert.False(NavmeshObbLookup.MayTouch(ref line, ref center, ref half));
        }

        [Fact]
        public void A_line_through_the_box_is_kept()
        {
            var line = new LineD(new Vector3D(-100, 1, 0), new Vector3D(100, 1, 0));
            var center = Vector3D.Zero;
            var half = new Vector3D(5, 5, 5);
            Assert.True(NavmeshObbLookup.MayTouch(ref line, ref center, ref half));
        }

        [Fact]
        public void Every_box_the_game_finds_is_kept()
        {
            // random turned boxes and lines: never a box the game's tests find left out by the cheap one
            var random = new Random(7);
            double R(double a) => (random.NextDouble() * 2 - 1) * a;
            var kept = 0;
            for (var n = 0; n < 200000; n++)
            {
                var orientation = Quaternion.CreateFromYawPitchRoll((float)R(3), (float)R(3), (float)R(3));
                var obb = new MyOrientedBoundingBoxD(new Vector3D(R(20), R(20), R(20)), new Vector3D(1 + random.NextDouble() * 8, 1 + random.NextDouble() * 3, 1 + random.NextDouble() * 8), orientation);
                var line = new LineD(new Vector3D(R(40), R(40), R(40)), new Vector3D(R(40), R(40), R(40)));
                if (!GameSays(obb, line)) continue;
                kept++;
                Assert.True(NavmeshObbLookup.MayTouch(ref line, ref obb.Center, ref obb.HalfExtent), $"c {obb.Center} h {obb.HalfExtent} o {obb.Orientation} line {line.From} {line.To} contains {obb.Contains(ref line.From)}/{obb.Contains(ref line.To)} hit {obb.Intersects(ref line)} len {line.Length}");
            }
            Assert.True(kept > 3000, "kept " + kept);
        }
    }
}
