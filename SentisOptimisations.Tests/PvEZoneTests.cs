using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using SentisGameplayImprovements.PveZone;
using Xunit;

namespace SentisOptimisations.Tests;

/// <summary>SentisGameplayImprovements' PvE zone: who may damage whose grid, the zone's settings, the game members it binds.</summary>
public class PvEZoneTests
{
    private const long Alice = 1001, Bob = 1002, Pirates = 1003;

    private static PvEParties Parties(long attacker = Bob, long victim = Alice) =>
        new PvEParties { Attacker = attacker, Victim = victim, AttackerSteamId = 76561198000000002, VictimSteamId = 76561198000000001 };

    [Fact]
    public void Another_player_is_stopped()
    {
        Assert.True(PvERules.Blocks(Parties(), npcDamageAllowed: false));
    }

    [Fact]
    public void Ones_own_grid_can_be_damaged()
    {
        Assert.False(PvERules.Blocks(Parties(Alice, Alice), false));
    }

    [Fact]
    public void The_same_player_under_another_identity_can_damage()
    {
        var p = Parties();
        p.AttackerSteamId = p.VictimSteamId;
        Assert.False(PvERules.Blocks(p, false));
    }

    [Fact]
    public void Two_players_without_steam_ids_are_not_the_same_player()
    {
        var p = Parties();
        p.AttackerSteamId = p.VictimSteamId = 0;
        Assert.True(PvERules.Blocks(p, false));
    }

    [Fact]
    public void A_faction_mate_can_damage()
    {
        var p = Parties();
        p.AttackerFactionId = p.VictimFactionId = 555;
        Assert.False(PvERules.Blocks(p, false));
    }

    [Fact]
    public void Another_faction_or_none_is_stopped()
    {
        var p = Parties();
        p.AttackerFactionId = 555;
        p.VictimFactionId = 777;
        Assert.True(PvERules.Blocks(p, false));
        p.AttackerFactionId = p.VictimFactionId = 0;
        Assert.True(PvERules.Blocks(p, false), "two players with no faction count as one faction");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void An_npc_on_either_side_goes_through_only_when_allowed(bool attackerIsNpc, bool victimIsNpc)
    {
        var p = Parties(attackerIsNpc ? Pirates : Bob, victimIsNpc ? Pirates : Alice);
        p.AttackerIsNpc = attackerIsNpc;
        p.VictimIsNpc = victimIsNpc;
        Assert.False(PvERules.Blocks(p, npcDamageAllowed: true));
        Assert.True(PvERules.Blocks(p, npcDamageAllowed: false));
    }

    [Theory]
    [InlineData(0, Alice)]
    [InlineData(Bob, 0)]
    public void An_unknown_side_goes_through(long attacker, long victim)
    {
        Assert.False(PvERules.Blocks(Parties(attacker, victim), false));
    }

    [Theory]
    [InlineData("-73641.29:-623775.25:-1089014.23", -73641.29, -623775.25, -1089014.23)]
    [InlineData("0:0:0", 0, 0, 0)]
    [InlineData("1e3:-2.5:3", 1000, -2.5, 3)]
    public void The_position_parses_with_a_dot_whatever_the_culture(string text, double x, double y, double z)
    {
        var culture = System.Threading.Thread.CurrentThread.CurrentCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
        try
        {
            Assert.True(PvECore.TryParsePosition(text, out var p));
            Assert.Equal(x, p.X, 6);
            Assert.Equal(y, p.Y, 6);
            Assert.Equal(z, p.Z, 6);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1:2")]
    [InlineData("1:2:3:4")]
    [InlineData("1,5:2:3")]
    [InlineData("a:b:c")]
    public void A_bad_position_does_not_parse(string text)
    {
        Assert.False(PvECore.TryParsePosition(text, out _));
    }

    [Theory]
    [InlineData("Container MK-1", true)]
    [InlineData("Pirate Container_MK-3", true)]
    [InlineData("Container MK", false)]
    [InlineData("My ship", false)]
    [InlineData(null, false)]
    public void Cargo_drop_containers_are_not_protected(string name, bool exempt)
    {
        Assert.Equal(exempt, PvECore.IsExempt(name));
    }

    /// <summary>
    /// Torch reads the config file before the plugin is up, through the setters - and the PvE ones reload
    /// the zone. An exception there made Torch drop the whole file for the defaults.
    /// </summary>
    [Fact]
    public void The_config_loads_before_the_plugin_is_up()
    {
        var example = System.IO.Path.Combine(SourceDir(), "..", "..", "SentisGameplayImprovements", "SentisGameplayImprovements.cfg.example");
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(SentisGameplayImprovements.MainConfig));
        using var reader = System.IO.File.OpenText(example);
        var config = (SentisGameplayImprovements.MainConfig)serializer.Deserialize(reader);
        Assert.False(config.PvEZoneEnabled);
        Assert.True(PvECore.TryParsePosition(config.PveZonePos, out _));
        config.PvEZoneEnabled = true;
        config.PveZonePos = "1:2:3";
        config.PveZoneRadius = 5;
    }

    private static string SourceDir([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        System.IO.Path.GetDirectoryName(path);

    [Fact]
    public void The_drill_target_has_the_parameters_the_patch_names()
    {
        var method = typeof(MyDrillBase).GetMethod("TryDrillBlocks", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method.ReturnType);
        var names = method.GetParameters().ToDictionary(p => p.Name, p => p.ParameterType);
        Assert.Equal(typeof(MyCubeGrid), names["grid"]);
        Assert.Equal(typeof(bool), names["onlyCheck"]);
        Assert.NotNull(typeof(MyDrillBase).GetField("m_drillEntity", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void The_ramming_target_exists()
    {
        var method = typeof(Sandbox.Game.Entities.Cube.MyGridPhysics).GetMethod("PerformDeformation", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.Contains(method.GetParameters(), p => p.Name == "otherEntity");
    }
}
