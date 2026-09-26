using System.Reflection;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class DrillCutPhysicsHandTests
    {
        [Fact]
        public void The_hand_cut_notify_is_found_in_the_game()
        {
            var field = typeof(Optimizer.Optimizations.DrillCutPhysics).GetField("HandCutNotify", BindingFlags.Static | BindingFlags.NonPublic);
            var lazy = field.GetValue(null);
            var value = lazy.GetType().GetProperty("Value").GetValue(lazy);
            Assert.NotNull(value);
        }
    }
}
