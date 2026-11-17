using System;
using System.Reflection;
using Optimizer.Optimizations;
using Sandbox.Game.Weapons.Guns;
using VRage.ModAPI;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class CapacitorPaceTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        [Fact]
        public void A_capacitor_not_charged_updates_every_100th_frame_and_not_at_all_switched_off()
        {
            Assert.Equal(MyEntityUpdateEnum.EACH_100TH_FRAME, CapacitorPace.Pace(true));
            Assert.Equal(MyEntityUpdateEnum.NONE, CapacitorPace.Pace(false));
            // no block yet: as one that is on
            Assert.Equal(MyEntityUpdateEnum.EACH_100TH_FRAME, CapacitorPace.Pace(null));
        }

        [Fact]
        public void The_games_capacitor_has_what_the_pacing_takes()
        {
            var type = typeof(MyEntityCapacitorComponent);
            Assert.NotNull(type.GetField("m_sink", Any));
            Assert.NotNull(type.GetField("m_functionalBlock", Any));
            Assert.NotNull(type.GetMethod("UpdateNotChargedState", Any, null, Type.EmptyTypes, null));
            Assert.NotNull(type.GetMethod("OnAddedToContainer", Any, null, Type.EmptyTypes, null));
            Assert.NotNull(type.GetMethod("UpdateAfterSimulation100", Any, null, Type.EmptyTypes, null));
            // the charge goes in once in 100 frames: what the pacing keeps
            Assert.NotNull(type.GetMethod("StorePower", Any, null, new[] { typeof(float), typeof(float) }, null));
        }
    }
}
