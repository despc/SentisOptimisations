using System;
using System.Collections.Generic;
using Optimizer.Optimizations;
using Sandbox.Game.Entities;
using VRageMath;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class ProjectionGeometryTests
    {
        [Theory]
        [InlineData(0.5f)]
        [InlineData(2.5f)]
        public void Moving_and_rotating_previews_keep_the_same_candidates(float gridSize)
        {
            var half = new Vector3(gridSize / 2);
            var random = new Random(147);
            for (var pose = 0; pose < 16; pose++)
            {
                var world = MatrixD.CreateFromYawPitchRoll(pose * 0.23, pose * -0.17, pose * 0.41);
                world.Translation = new Vector3D(1000000 + pose * 1.7, -10000, 42000);
                var geometry = new WelderOptimization.ProjectionGeometry(ref world, gridSize, half);
                var sphere = new BoundingSphereD(world.Translation + new Vector3D(0.5, -1.5, 4.5), 12);
                var expected = new List<int>();
                var actual = new List<int>();
                for (var i = 0; i < 240; i++)
                {
                    var min = new Vector3I(random.Next(-12, 13), random.Next(-12, 13), random.Next(-12, 13));
                    var max = min + new Vector3I(i % 4, i % 3, i % 5);
                    var original = new BoundingBoxD(min * gridSize - half, max * gridSize + half).TransformFast(world);
                    var transformed = geometry.BlockBounds(min, max);
                    Assert.Equal(original.Min, transformed.Min);
                    Assert.Equal(original.Max, transformed.Max);
                    var oldCenter = MyCubeGrid.GridIntegerToWorld(gridSize, min, world);
                    var newCenter = geometry.CellCenter(min);
                    Assert.Equal(oldCenter, newCenter);
                    if (original.Intersects(sphere)) expected.Add(i);
                    if (transformed.Intersects(sphere)) actual.Add(i);
                }
                Assert.NotEmpty(expected);
                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public void Non_affine_matrix_uses_the_games_full_transform()
        {
            var world = MatrixD.Identity;
            world.M14 = 0.1;
            var geometry = new WelderOptimization.ProjectionGeometry(ref world, 2.5f, new Vector3(1.25f));
            var cell = new Vector3I(4, -2, 6);
            Assert.Equal(MyCubeGrid.GridIntegerToWorld(2.5f, cell, world), geometry.CellCenter(cell));
        }
    }
}
