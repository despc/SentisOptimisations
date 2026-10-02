using System.IO;
using System.Xml.Serialization;
using SentisOptimisationsPlugin;
using Xunit;

namespace SentisOptimisations.Tests
{
    /// <summary>The config is read at Init, before there is a game: its setters must not need one.</summary>
    public class MainConfigLoadTests
    {
        [Fact]
        public void Config_with_freeze_physics_loads_without_a_game()
        {
            // FreezePhysics' setter called the game from the config load: an exception there failed the whole load and
            // Torch went on with the defaults (01.10.2026).
            const string xml = "<?xml version=\"1.0\"?><MainConfig><FreezePhysics>true</FreezePhysics></MainConfig>";
            var config = (MainConfig)new XmlSerializer(typeof(MainConfig)).Deserialize(new StringReader(xml));
            Assert.True(config.FreezePhysics);
        }

        [Fact]
        public void Game_thread_on_fast_cores_is_on_unless_the_config_turns_it_off()
        {
            var serializer = new XmlSerializer(typeof(MainConfig));
            var old = (MainConfig)serializer.Deserialize(new StringReader("<?xml version=\"1.0\"?><MainConfig></MainConfig>"));
            Assert.True(old.GameThreadOnFastCores);
            var off = (MainConfig)serializer.Deserialize(new StringReader(
                "<?xml version=\"1.0\"?><MainConfig><GameThreadOnFastCores>false</GameThreadOnFastCores></MainConfig>"));
            Assert.False(off.GameThreadOnFastCores);
        }
    }
}
