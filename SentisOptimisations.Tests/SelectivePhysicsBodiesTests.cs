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
        public void Without_selective_updates_every_cluster_is_walked()
        {
            Assert.True(SelectivePhysicsBodies.Walk(selective: false, clusterStepped: false));
        }
    }
}
