using SentisOptimisations.Commands;
using Xunit;
using R = SentisOptimisations.Commands.GpsCleanCommands.Reason;

namespace SentisOptimisations.Tests
{
    public class GpsCleanTests
    {
        private static GpsCleanCommands.Facts Own(string name = "База") =>
            new GpsCleanCommands.Facts { OwnerExists = true, Name = name, ContractsKnown = true };

        private static readonly GpsCleanCommands.Options None = new GpsCleanCommands.Options();

        [Fact]
        public void A_players_own_mark_stays()
        {
            Assert.Equal(R.Keep, GpsCleanCommands.Judge(Own(), None));
            Assert.Equal(R.Keep, GpsCleanCommands.Judge(Own(), new GpsCleanCommands.Options { Signals = true, Linked = true }));
        }

        [Fact]
        public void The_marks_of_a_gone_identity_go()
        {
            var f = Own(); f.OwnerExists = false;
            Assert.Equal(R.OwnerGone, GpsCleanCommands.Judge(f, None));
        }

        [Fact]
        public void An_encounter_signal_goes_with_its_encounter_and_on_signals()
        {
            var f = Own("Unidentified Signal"); f.Encounter = true; f.EntityId = 5; f.EntityAlive = true;
            Assert.Equal(R.Keep, GpsCleanCommands.Judge(f, None));
            Assert.Equal(R.Signal, GpsCleanCommands.Judge(f, new GpsCleanCommands.Options { Signals = true }));
            f.EntityAlive = false;
            Assert.Equal(R.SignalGone, GpsCleanCommands.Judge(f, None));
        }

        [Fact]
        public void A_contract_mark_goes_once_the_contract_is_over_and_only_when_contracts_are_known()
        {
            var f = Own("Acquisition Drop-Off"); f.ContractId = 7; f.ContractActive = true;
            Assert.Equal(R.Keep, GpsCleanCommands.Judge(f, None));
            f.ContractActive = false;
            Assert.Equal(R.ContractOver, GpsCleanCommands.Judge(f, None));
            f.ContractsKnown = false;
            Assert.Equal(R.Keep, GpsCleanCommands.Judge(f, None));
        }

        [Fact]
        public void A_linked_mark_of_a_gone_entity_goes_only_on_linked()
        {
            var f = Own("Мой корабль"); f.EntityId = 9; f.EntityAlive = false;
            Assert.Equal(R.Keep, GpsCleanCommands.Judge(f, None));
            Assert.Equal(R.LinkedGone, GpsCleanCommands.Judge(f, new GpsCleanCommands.Options { Linked = true }));
        }

        [Fact]
        public void A_duplicate_and_a_named_mark_go()
        {
            var f = Own(); f.Duplicate = true;
            Assert.Equal(R.Duplicate, GpsCleanCommands.Judge(f, None));
            Assert.Equal(R.Named, GpsCleanCommands.Judge(Own("Аномальная Зона"), new GpsCleanCommands.Options { Name = " аномальная зона" }));
        }

        [Fact]
        public void The_line_is_read()
        {
            var o = GpsCleanCommands.Parse(new[] { "clean", "signals", "name", "Дар", "аномалии", "apply" }, out var e);
            Assert.Null(e);
            Assert.True(o.Signals);
            Assert.True(o.Apply);
            Assert.Equal("Дар аномалии", o.Name);
            Assert.Null(GpsCleanCommands.Parse(new[] { "wat" }, out e));
            Assert.NotNull(e);
        }
    }
}
