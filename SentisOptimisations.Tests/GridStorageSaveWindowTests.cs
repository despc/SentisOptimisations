using SentisOptimisationsPlugin;
using Xunit;

namespace SentisOptimisations.Tests
{
    /// <summary>The window of GridStorageSaveWindow: when the grid storage refuses to store for a save.</summary>
    public class GridStorageSaveWindowTests
    {
        private const long Max = 1000;

        private static GridStorageSaveWindow.SaveWindow New() => new GridStorageSaveWindow.SaveWindow(Max);

        [Fact]
        public void Closed_before_any_save()
        {
            Assert.False(New().IsOpen(0));
        }

        [Fact]
        public void Open_during_a_save_and_closed_after_it()
        {
            var window = New();
            window.Open(10);
            Assert.True(window.IsOpen(10));
            Assert.True(window.IsOpen(10 + Max));
            window.Close();
            Assert.False(window.IsOpen(20));
        }

        [Fact]
        public void Overlapping_saves_keep_it_open_until_the_last_ends()
        {
            var window = New();
            window.Open(10);
            window.Open(20);
            window.Close();
            Assert.True(window.IsOpen(30));
            window.Close();
            Assert.False(window.IsOpen(30));
        }

        [Fact]
        public void A_save_that_never_ends_holds_it_no_longer_than_the_limit()
        {
            var window = New();
            window.Open(10);                        // threw: no Close
            Assert.True(window.IsOpen(10 + Max));
            Assert.False(window.IsOpen(11 + Max));
        }

        [Fact]
        public void The_next_save_after_one_that_never_ended_closes_normally()
        {
            var window = New();
            window.Open(10);                        // threw: no Close
            window.Open(10 + 5 * Max);
            Assert.True(window.IsOpen(10 + 5 * Max));
            window.Close();
            Assert.False(window.IsOpen(11 + 5 * Max));
        }

        [Fact]
        public void An_extra_close_does_not_break_the_next_save()
        {
            var window = New();
            window.Close();
            window.Open(10);
            Assert.True(window.IsOpen(10));
            window.Close();
            Assert.False(window.IsOpen(10));
        }
    }
}
