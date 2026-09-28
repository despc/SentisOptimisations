using SentisOptimisationsPlugin.Freezer;
using VRage.ModAPI;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class FrozenUpdateGuardTests
    {
        [Fact]
        public void Only_entities_the_freezer_marked_are_kept_out()
        {
            Assert.True(FrozenUpdateGuard.Admit(EntityFlags.Visible | EntityFlags.Save | EntityFlags.NeedsUpdate));
            Assert.False(FrozenUpdateGuard.Admit(EntityFlags.Visible | FrozenUpdateGuard.FrozenMark));
        }
    }
}
