using System.Linq;
using SentisOptimisationsPlugin;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class FloatingInVoxelCheckTests
    {
        [Fact]
        public void The_check_runs_on_one_frame_in_four()
        {
            Assert.Equal(25, Enumerable.Range(0, 100).Count(FloatingInVoxelCheck.RunsOn));
        }

        [Fact]
        public void The_check_runs_on_the_first_frame()
        {
            Assert.True(FloatingInVoxelCheck.RunsOn(0));
        }
    }
}
