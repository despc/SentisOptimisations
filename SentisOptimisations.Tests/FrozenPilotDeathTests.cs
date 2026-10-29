using System.Reflection;
using Sandbox.Game.Components;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using VRage.Game.ModAPI;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class FrozenPilotDeathTests
    {
        [Fact]
        public void Kill_with_damage_information_is_the_target()
        {
            var kill = typeof(MyCharacter).GetMethod(nameof(MyCharacter.Kill), BindingFlags.Instance | BindingFlags.Public,
                null, new[] { typeof(bool), typeof(MyDamageInformation) }, null);
            Assert.NotNull(kill);
        }

        // Why a frozen character starves at all: its stats are not updated with the entity, so taking it off the
        // updates does not stop them. If the game ever moves them back onto the entity, the patch has nothing to do.
        [Fact]
        public void Character_stats_update_apart_from_the_entity()
        {
            Assert.True(typeof(MyUpdatingEntityComponent).IsAssignableFrom(typeof(MyCharacterStatComponent)));
        }

        // The death itself is deferred to the character's own update; Kill only raises this flag.
        [Fact]
        public void Kill_defers_the_death_to_the_update()
        {
            Assert.NotNull(typeof(MyCharacter).GetField("m_dieAfterSimulation", BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.NotNull(typeof(MyCharacter).GetMethod("UpdateDying", BindingFlags.Instance | BindingFlags.NonPublic));
        }
    }
}
