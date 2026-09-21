using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests
{
    /// <summary>When a piston's per-frame update may be skipped because it cannot change anything.</summary>
    public class PistonUpdateTests
    {
        [Fact]
        public void Extending_against_the_upper_limit_is_at_rest() =>
            Assert.True(PistonUpdate.AtRest(0.5f, 0.5f, 10f, 0f, 10f));

        [Fact]
        public void Retracting_against_the_lower_limit_is_at_rest() =>
            Assert.True(PistonUpdate.AtRest(-0.5f, -0.5f, 0f, 0f, 10f));

        [Fact]
        public void Standing_still_in_between_is_at_rest() =>
            Assert.True(PistonUpdate.AtRest(0f, 0f, 4.2f, 0f, 10f));

        [Fact]
        public void Anything_on_its_way_is_not() =>
            Assert.False(PistonUpdate.AtRest(0.5f, 0.5f, 4.2f, 0f, 10f));

        [Fact]
        public void Leaving_a_limit_is_not()
        {
            // at the top, now told to go down: that is movement
            Assert.False(PistonUpdate.AtRest(-0.5f, -0.5f, 10f, 0f, 10f));
            Assert.False(PistonUpdate.AtRest(0.5f, 0.5f, 0f, 0f, 10f));
        }

        [Fact]
        public void The_frame_the_direction_flips_is_left_to_the_game()
        {
            // the game notes the flip and ignores sideways forces for a few frames
            Assert.False(PistonUpdate.AtRest(0.5f, -0.5f, 10f, 0f, 10f));
            Assert.False(PistonUpdate.AtRest(0f, 0.5f, 4.2f, 0f, 10f));
        }
    }
}
