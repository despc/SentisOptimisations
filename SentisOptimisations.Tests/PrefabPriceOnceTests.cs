using System.Collections.Generic;
using SentisOptimisationsPlugin;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class PrefabPriceOnceTests
    {
        private static readonly Dictionary<string, float> Done = new Dictionary<string, float> { ["Pirate A"] = 1f, ["Hauler"] = 2f };
        private static bool Known(string n) => n == "Pirate A" || n == "Hauler";

        [Fact]
        public void A_prefab_worked_out_with_the_same_multiplier_is_not_asked_again()
        {
            Assert.Empty(PrefabPriceOnce.StillToDo(new[] { "Pirate A" }, 1f, Done, Known));
        }

        [Fact]
        public void A_prefab_worked_out_with_another_multiplier_is_asked_again()
        {
            Assert.Equal(new[] { "Hauler" }, PrefabPriceOnce.StillToDo(new[] { "Hauler" }, 1f, Done, Known));
        }

        [Fact]
        public void A_new_prefab_is_asked_once_even_when_named_twice()
        {
            Assert.Equal(new[] { "New" }, PrefabPriceOnce.StillToDo(new[] { "Pirate A", "New", "New" }, 1f, Done, Known));
        }

        [Fact]
        public void A_prefab_marked_done_but_not_kept_by_the_calculator_is_asked_again()
        {
            Assert.Equal(new[] { "Pirate A" }, PrefabPriceOnce.StillToDo(new[] { "Pirate A" }, 1f, Done, n => false));
        }
    }
}
