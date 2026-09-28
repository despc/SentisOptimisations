using SentisOptimisationsPlugin.CrashFix;
using VRageMath;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class PlanetPhysicsBoxTests
    {
        private static readonly BoundingBoxD Planet = new BoundingBoxD(new Vector3D(-60000), new Vector3D(60000));

        [Fact]
        public void A_huge_box_is_cut_to_the_planet()
        {
            var box = new BoundingBoxD(new Vector3D(-20000000), new Vector3D(20000000));
            Assert.True(PlanetPhysicsBox.Cut(ref box, Planet));
            Assert.True(box.Size.Max() < 130000);
        }

        [Fact]
        public void A_box_inside_the_planet_stays_as_it_is()
        {
            var box = new BoundingBoxD(new Vector3D(100), new Vector3D(700));
            var before = box;
            Assert.True(PlanetPhysicsBox.Cut(ref box, Planet));
            Assert.Equal(before, box);
        }

        [Fact]
        public void A_box_away_from_the_planet_builds_nothing()
        {
            var box = new BoundingBoxD(new Vector3D(500000), new Vector3D(501000));
            Assert.False(PlanetPhysicsBox.Cut(ref box, Planet));
        }

        [Fact]
        public void Only_boxes_up_to_a_thousand_km_are_sane()
        {
            Assert.True(PlanetPhysicsBox.Sane(new BoundingBoxD(new Vector3D(-5000), new Vector3D(5000))));
            Assert.False(PlanetPhysicsBox.Sane(new BoundingBoxD(new Vector3D(-20000000), new Vector3D(20000000))));
            Assert.False(PlanetPhysicsBox.Sane(new BoundingBoxD(new Vector3D(double.NaN), new Vector3D(double.NaN))));
        }

        [Fact]
        public void A_scaled_character_matrix_is_straightened_in_place()
        {
            // [BOT] Cedar's matrix on the stand, 28.09.2026: a 1.8 m character with a 45 000 km box
            var m = new MatrixD(-18305.58203125, 596858.5, -270360.5, 0, 650236.5, -21730058, 6115835, 0,
                80791.9609375, -6121275, -21747538, 0, -698373.107646596, 19470576.8590722, -5732599.12765914, 1);
            Assert.True(PlanetPhysicsBox.Scaled(m));
            var s = PlanetPhysicsBox.Straightened(m);
            Assert.False(PlanetPhysicsBox.Scaled(s));
            Assert.Equal(0, Vector3D.Distance(m.Translation, s.Translation), 6);
            Assert.True(Vector3D.Dot(Vector3D.Normalize(m.Up), s.Up) > 0.999);
            Assert.True(Vector3D.Dot(s.Up, s.Forward) < 1e-9);
            Assert.False(PlanetPhysicsBox.Scaled(MatrixD.CreateWorld(new Vector3D(5), Vector3D.Forward, Vector3D.Up)));
        }
    }
}
