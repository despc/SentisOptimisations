using SentisOptimisationsPlugin;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class EntityDeleteBudgetTests
    {
        [Fact]
        public void The_first_entity_is_deleted_whatever_the_time()
        {
            Assert.True(EntityDeleteBudget.GoOn(0, 1000, 10));
        }

        [Fact]
        public void More_are_deleted_while_there_is_time()
        {
            Assert.True(EntityDeleteBudget.GoOn(5, 9, 10));
        }

        [Fact]
        public void The_rest_waits_once_the_time_is_spent()
        {
            Assert.False(EntityDeleteBudget.GoOn(5, 10, 10));
        }
    }
}
