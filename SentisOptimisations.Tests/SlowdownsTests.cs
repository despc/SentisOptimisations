using System;
using System.Linq;
using Sandbox.ModAPI.Ingame;
using SentisOptimisations.Slowdowns;
using Xunit;

namespace SentisOptimisations.Tests
{
    [Collection("PeakLoad")]
    public class PeakLoadTests
    {
        private static PeakLoad.Settings Settings(bool enabled = true, bool always = false) => new PeakLoad.Settings
        {
            Enabled = enabled, Always = always, Percent = 70, OffPercent = 60,
        };

        [Fact]
        public void The_slowdowns_go_on_over_the_on_threshold_and_off_under_the_off_one()
        {
            PeakLoad.Reset();
            var s = Settings();
            PeakLoad.Second(65, 1, s);   // between the two, from off: stays off
            Assert.False(PeakLoad.Active);
            PeakLoad.Second(71, 1, s);
            Assert.True(PeakLoad.Active);
            Assert.True(PeakLoad.Busy);
            PeakLoad.Second(65, 1, s);   // between the two, from on: stays on
            PeakLoad.Second(60, 1, s);
            Assert.True(PeakLoad.Active);
            PeakLoad.Second(59, 1, s);
            Assert.False(PeakLoad.Active);
            PeakLoad.Second(250, 0.4, s);
            Assert.True(PeakLoad.Active);
        }

        [Fact]
        public void Always_keeps_them_on_and_the_master_switch_keeps_them_off()
        {
            PeakLoad.Reset();
            PeakLoad.Second(10, 1, Settings(always: true));
            Assert.True(PeakLoad.Active);
            PeakLoad.Second(500, 0.1, Settings(enabled: false, always: true));
            Assert.False(PeakLoad.Active);
            PeakLoad.Second(500, 0.1, Settings(enabled: false));
            Assert.False(PeakLoad.Active);
        }

        [Fact]
        public void The_load_and_speed_of_a_second_are_its_frames()
        {
            PeakLoad.Reset();
            var saved = PeakLoad.Config;
            PeakLoad.Config = () => Settings();
            try
            {
                PeakLoad.Tick(0, 0);
                // 30 frames in a second, each 200% of its time: half speed
                for (var i = 1; i <= 30; i++) PeakLoad.Tick(i * 1000 / 30, 200);
                Assert.Equal(0.5, PeakLoad.LastSpeed, 2);
                Assert.Equal(200, PeakLoad.LastLoad, 2);
                Assert.True(PeakLoad.Busy);
            }
            finally
            {
                PeakLoad.Config = saved;
                PeakLoad.Reset();
            }
        }

        // entity ids as the game makes them: random 56 bits, the id's type in the top 8
        private static long[] GameIds(int n, int seed = 1)
        {
            var random = new Random(seed);
            var bytes = new byte[8];
            return Enumerable.Range(0, n).Select(_ =>
            {
                random.NextBytes(bytes);
                return (long)((BitConverter.ToUInt64(bytes, 0) & 0x00FFFFFFFFFFFFFFUL) | (1UL << 56));
            }).ToArray();
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void An_entity_has_one_turn_in_a_period_and_entities_are_spread(int period)
        {
            foreach (var id in GameIds(50))
                Assert.Equal(1, Enumerable.Range(0, period).Count(f => PeakLoad.Turn(id, (ulong)(1000 + f), period)));
            Assert.True(PeakLoad.Turn(5, 3, 1));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void The_turrets_of_a_base_are_spread_over_the_3_frames_not_in_one(int seed)
        {
            // game ids, and ids made one after another (the game's sequential ones): each frame gets about a third
            foreach (var ids in new[] { GameIds(300, seed), Enumerable.Range(0, 300).Select(i => 72057594037927936L + i * 1000 + seed).ToArray() })
            {
                var perFrame = Enumerable.Range(0, IdleTurretSlowdown.Period)
                    .Select(f => ids.Count(id => PeakLoad.Turn(id, (ulong)(5000 + f), IdleTurretSlowdown.Period))).ToArray();
                Assert.Equal(300, perFrame.Sum());
                Assert.All(perFrame, n => Assert.InRange(n, 70, 130));
            }
        }

        [Fact]
        public void A_script_far_from_players_gets_Update1_once_in_3_frames()
        {
            foreach (var id in GameIds(20))
            {
                var given = Enumerable.Range(0, 300).Count(f => (ScriptSlowdown.Thin(UpdateType.Update1, id, (ulong)f, false) & UpdateType.Update1) != 0);
                Assert.Equal(100, given);
                // near a player: every frame
                Assert.All(Enumerable.Range(0, 300), f => Assert.Equal(UpdateType.Update1, ScriptSlowdown.Thin(UpdateType.Update1, id, (ulong)f, true)));
            }
        }

        [Fact]
        public void Update10_runs_every_30_frames_and_Update100_every_300_players_near_or_not()
        {
            foreach (var id in GameIds(20))
            foreach (var near in new[] { false, true })
            {
                // the game gives a block its Update10 on frames 10 apart, Update100 on frames 100 apart
                var tens = Enumerable.Range(0, 300).Select(i => (ulong)(i * 10 + 7)).Where(f => (ScriptSlowdown.Thin(UpdateType.Update10, id, f, near) & UpdateType.Update10) != 0).ToArray();
                Assert.Equal(100, tens.Length);
                Assert.All(tens.Zip(tens.Skip(1), (a, b) => b - a), d => Assert.Equal(30UL, d));
                var hundreds = Enumerable.Range(0, 30).Select(i => (ulong)(i * 100 + 42)).Where(f => (ScriptSlowdown.Thin(UpdateType.Update100, id, f, near) & UpdateType.Update100) != 0).ToArray();
                Assert.Equal(10, hundreds.Length);
                Assert.All(hundreds.Zip(hundreds.Skip(1), (a, b) => b - a), d => Assert.Equal(300UL, d));
            }
        }

        [Fact]
        public void Once_is_always_given_and_the_rest_thinned_apart()
        {
            const long id = 987654321;
            for (ulong f = 0; f < 300; f++)
            {
                var left = ScriptSlowdown.Thin(UpdateType.Once | UpdateType.Update1 | UpdateType.Update10, id, f, false);
                Assert.True((left & UpdateType.Once) != 0);
            }
        }

        [Fact]
        public void A_projection_with_no_welder_near_moves_once_a_second()
        {
            long last = -1000;
            var moves = 0;
            for (long f = 0; f < 600; f++)
                if (!ProjectionSlowdown.IdleSkips(f, last)) { moves++; last = f; }
            Assert.Equal(10, moves);
            Assert.False(ProjectionSlowdown.IdleSkips(0, long.MinValue / 2));
        }

    }
}
