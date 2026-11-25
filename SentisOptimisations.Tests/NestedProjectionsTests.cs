using System.Collections.Generic;
using Sandbox.Common.ObjectBuilders;
using SentisOptimisationsPlugin.Perfomance;
using VRage.Game;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class NestedProjectionsTests
    {
        private static MyObjectBuilder_CubeGrid Grid(params MyObjectBuilder_CubeBlock[] blocks) =>
            new MyObjectBuilder_CubeGrid { CubeBlocks = new List<MyObjectBuilder_CubeBlock>(blocks) };

        private static MyObjectBuilder_Projector Projector(params MyObjectBuilder_CubeGrid[] blueprint) =>
            new MyObjectBuilder_Projector { ProjectedGrids = new List<MyObjectBuilder_CubeGrid>(blueprint) };

        [Fact]
        public void The_projectors_inside_a_blueprint_lose_theirs_the_blueprint_stays()
        {
            var deepest = Grid(new MyObjectBuilder_CubeBlock());
            var inner = Projector(Grid(Projector(deepest)), Grid());
            var legacy = new MyObjectBuilder_Projector { ProjectedGrid = Grid() };
            var blueprint = new List<MyObjectBuilder_CubeGrid> { Grid(inner, new MyObjectBuilder_CubeBlock()), Grid(legacy) };
            Assert.Equal(3, NestedProjections.Strip(blueprint));
            Assert.Null(inner.ProjectedGrids);
            Assert.Null(legacy.ProjectedGrid);
            Assert.Equal(2, blueprint.Count);
            Assert.Equal(2, blueprint[0].CubeBlocks.Count);
            Assert.Equal(0, NestedProjections.Strip(blueprint));
        }

        [Fact]
        public void A_projectors_own_blueprint_is_kept_only_its_inside_goes()
        {
            var inner = Projector(Grid());
            var own = Projector(Grid(inner));
            Assert.Equal(1, NestedProjections.StripInside(own));
            Assert.Single(own.ProjectedGrids);
            Assert.Null(inner.ProjectedGrids);
            Assert.Equal(0, NestedProjections.StripInside(Projector()));
            Assert.Equal(0, NestedProjections.Strip(null));
        }
    }
}
