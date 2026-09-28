using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class SelectivePhysicsBodiesTests
    {
        [Fact]
        public void A_cluster_the_game_does_not_step_is_not_walked()
        {
            Assert.False(SelectivePhysicsBodies.Walk(selective: true, clusterStepped: false));
        }

        [Fact]
        public void A_stepped_cluster_is_walked()
        {
            Assert.True(SelectivePhysicsBodies.Walk(selective: true, clusterStepped: true));
        }

        [Fact]
        public void A_character_nobody_controls_does_not_keep_its_cluster_stepped()
        {
            Assert.False(SelectivePhysicsBodies.Stepped(dead: false, controlled: false));
        }

        [Fact]
        public void A_controlled_character_keeps_its_cluster_stepped()
        {
            Assert.True(SelectivePhysicsBodies.Stepped(dead: false, controlled: true));
        }

        [Fact]
        public void A_dead_character_does_not_keep_its_cluster_stepped()
        {
            Assert.False(SelectivePhysicsBodies.Stepped(dead: true, controlled: true));
        }

        [Fact]
        public void Without_selective_updates_every_cluster_is_walked()
        {
            Assert.True(SelectivePhysicsBodies.Walk(selective: false, clusterStepped: false));
        }
    }
}
