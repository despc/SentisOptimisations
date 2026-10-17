using SentisOptimisationsPlugin;
using Xunit;

namespace SentisOptimisations.Tests
{
    /// <summary>The window of GridStorageSaveWindow: when the grid storage refuses to store for a save.</summary>
    public class GridStorageSaveWindowTests
    {
        [Fact]
        public void Closed_before_any_save()
        {
            Assert.False(new GridStorageSaveWindow.SaveWindow().IsOpen);
        }

        [Fact]
        public void Open_during_a_save_and_closed_after_it()
        {
            var window = new GridStorageSaveWindow.SaveWindow();
            window.Open();
            Assert.True(window.IsOpen);
            window.Close();
            Assert.False(window.IsOpen);
        }

        [Fact]
        public void Overlapping_saves_keep_it_open_until_the_last_ends()
        {
            var window = new GridStorageSaveWindow.SaveWindow();
            window.Open();
            window.Open();
            window.Close();
            Assert.True(window.IsOpen);
            window.Close();
            Assert.False(window.IsOpen);
        }

        [Fact]
        public void An_extra_close_does_not_break_the_next_save()
        {
            var window = new GridStorageSaveWindow.SaveWindow();
            window.Close();
            window.Open();
            Assert.True(window.IsOpen);
            window.Close();
            Assert.False(window.IsOpen);
        }
    }
}
