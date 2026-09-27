using SentisOptimisationsPlugin;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class EconomySpreadTests
    {
        [Fact]
        public void The_economy_methods_are_found_in_the_game()
        {
            Assert.True(EconomySpread.Bind());
        }
    }
}
