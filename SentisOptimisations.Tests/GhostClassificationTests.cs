using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using SentisOptimisations.Commands;
using Xunit;
using Kind = SentisOptimisations.Commands.CharacterGhosts.Kind;

namespace SentisOptimisations.Tests
{
    /// <summary>!ai_npc ghosts and the guards for a character without an identity (25.11.2026).</summary>
    public class GhostClassificationTests
    {
        [Theory]
        // no identity: a ghost whatever else is known about it
        [InlineData(false, false, false, false, Kind.NoIdentity)]
        [InlineData(false, true, true, true, Kind.NoIdentity)]
        // an identity that controls or holds the body: not a ghost
        [InlineData(true, true, false, false, Kind.None)]
        [InlineData(true, false, true, false, Kind.None)]
        [InlineData(true, false, false, true, Kind.None)]
        // an identity that neither controls nor holds it: a reused number, or a body made without ChangeCharacter
        [InlineData(true, false, false, false, Kind.NotHeld)]
        public void Characters_are_classified_by_their_identity(bool identity, bool controlled, bool held, bool saved, Kind expected)
        {
            Assert.Equal(expected, CharacterGhosts.Classify(identity, controlled, held, saved));
        }

        [Fact]
        public void A_plain_clean_removes_only_bodies_without_an_identity()
        {
            Assert.True(CharacterGhosts.Removes(Kind.NoIdentity, false));
            Assert.False(CharacterGhosts.Removes(Kind.NotHeld, false));
            Assert.False(CharacterGhosts.Removes(Kind.None, false));
        }

        [Fact]
        public void Clean_all_also_removes_bodies_their_identity_does_not_hold()
        {
            Assert.True(CharacterGhosts.Removes(Kind.NoIdentity, true));
            Assert.True(CharacterGhosts.Removes(Kind.NotHeld, true));
            Assert.False(CharacterGhosts.Removes(Kind.None, true));
        }

        [Fact]
        public void The_cryo_guard_binds_to_the_games_RemovePlayer_by_name()
        {
            // Torch binds a prefix's parameters by name: a renamed parameter is a FATAL at startup ("Parameter name ... not found")
            var target = typeof(MyPlayerCollection).GetMethod(nameof(MyPlayerCollection.RemovePlayer),
                BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(MyPlayer), typeof(bool) }, null);
            Assert.NotNull(target);
            var prefix = typeof(SentisOptimisationsPlugin.CrashFix.CrashFixPatch).GetMethod("BotOutOfCryoBeforeRemoval", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(prefix);
            var game = target.GetParameters().ToDictionary(p => p.Name, p => p.ParameterType);
            foreach (var parameter in prefix.GetParameters())
            {
                Assert.True(game.TryGetValue(parameter.Name, out var type), "RemovePlayer has no parameter '" + parameter.Name + "'");
                Assert.Equal(type, parameter.ParameterType);
            }
            Assert.Equal(typeof(void), prefix.ReturnType);
        }

        [Fact]
        public void The_weapon_position_guard_has_its_target()
        {
            var update = typeof(MyCharacterWeaponPositionComponent).GetMethod(nameof(MyCharacterWeaponPositionComponent.Update),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            Assert.NotNull(update);
            var guard = typeof(SentisOptimisationsPlugin.CrashFix.CrashFixPatch).GetMethod("WeaponPositionOfCharacterWithIdentity", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(guard);
            Assert.Equal(typeof(bool), guard.ReturnType);
            Assert.Equal(typeof(MyCharacterWeaponPositionComponent), guard.GetParameters().Single(p => p.Name == "__instance").ParameterType);
        }
    }
}
