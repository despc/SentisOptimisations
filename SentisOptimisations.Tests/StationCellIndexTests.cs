using System.Collections.Generic;
using SentisOptimisationsPlugin;
using VRageMath;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class StationCellIndexTests
    {
        [Fact]
        public void A_station_inside_a_cell_is_in_that_cell_only()
        {
            var cells = new HashSet<Vector3I>();
            StationCellIndex.AddCellsOf(new Vector3D(1500, -2500, 10), 1000, cells);
            Assert.Equal(new HashSet<Vector3I> { new Vector3I(1, -3, 0) }, cells);
        }

        [Fact]
        public void A_station_on_a_border_is_in_the_cells_on_both_sides()
        {
            var cells = new HashSet<Vector3I>();
            StationCellIndex.AddCellsOf(new Vector3D(2000, 500, -1000), 1000, cells);
            Assert.Equal(new HashSet<Vector3I> { new Vector3I(2, 0, -1), new Vector3I(1, 0, -1), new Vector3I(2, 0, -2), new Vector3I(1, 0, -2) }, cells);
        }

        [Fact]
        public void Each_cell_found_holds_the_station_as_the_game_tests_it()
        {
            foreach (var at in new[] { new Vector3D(2000, 500, -1000), new Vector3D(-0.5, 0, 999.9), new Vector3D(123456.7, -98765.4, 0) })
            {
                var cells = new HashSet<Vector3I>();
                StationCellIndex.AddCellsOf(at, 1000, cells);
                foreach (var cell in cells)
                    Assert.Equal(ContainmentType.Contains, new BoundingBoxD(cell * 1000.0, (cell + 1) * 1000.0).Contains(at));
            }
        }
    }
}
