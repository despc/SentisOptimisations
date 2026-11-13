using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests
{
    /// <summary>
    /// GridStreamPacing's bounds-checked queue membership: the game's FastPriorityQueue.Contains read the node array at the
    /// node's index unchecked, and an entry kept from a client's previous connection threw IndexOutOfRangeException on
    /// every stream of that client (production, 13.11.2026).
    /// </summary>
    public class GridStreamPacingHoldsTests
    {
        [Fact]
        public void A_node_at_its_index_is_held()
        {
            var node = new object();
            var nodes = new object[8];
            nodes[3] = node;
            Assert.True(GridStreamPacing.Holds(nodes, 3, node));
        }

        [Theory]
        [InlineData(0)]      // a node never queued, or taken out
        [InlineData(-1)]
        [InlineData(8)]      // past the array: a node of the client's previous, bigger queue
        [InlineData(4000)]
        [InlineData(5)]      // another node there
        public void Anything_else_is_not_held_and_nothing_throws(int index)
        {
            var nodes = new object[8];
            nodes[5] = new object();
            Assert.False(GridStreamPacing.Holds(nodes, index, new object()));
        }

        [Fact]
        public void No_queue_or_no_node_is_not_held()
        {
            Assert.False(GridStreamPacing.Holds(null, 1, new object()));
            Assert.False(GridStreamPacing.Holds(new object[4], 1, null));
        }
    }
}
