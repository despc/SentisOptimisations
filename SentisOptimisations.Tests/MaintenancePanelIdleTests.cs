using System;
using System.Reflection;
using SentisOptimisationsPlugin;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class MaintenancePanelIdleTests
    {
        [Fact]
        public void A_dedicated_server_skips_a_panel_with_nothing_pending()
        {
            Assert.Equal(MaintenancePanelIdle.Action.Skip, MaintenancePanelIdle.Decide(dedicated: true, gameWork: false, instant: false));
        }

        [Fact]
        public void The_instant_update_finishes_the_move_once_instead_of_looping()
        {
            Assert.Equal(MaintenancePanelIdle.Action.FinishMove, MaintenancePanelIdle.Decide(dedicated: true, gameWork: false, instant: true));
        }

        [Fact]
        public void The_games_own_work_runs_the_game_update()
        {
            Assert.Equal(MaintenancePanelIdle.Action.Game, MaintenancePanelIdle.Decide(dedicated: true, gameWork: true, instant: true));
        }

        [Fact]
        public void The_detector_move_a_left_out_pass_does_is_the_games()
        {
            // the game's pass puts the use object of a cover that starts opened (16.11.2026): a pass left out does it too
            var type = typeof(SpaceEngineers.Game.EntityComponents.Blocks.MyMaintenancePanelComponent);
            Assert.NotNull(type.GetMethod("MoveDetectorToInitialOpenedRotation", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null));
        }

        [Fact]
        public void A_client_always_runs_the_game_update()
        {
            Assert.Equal(MaintenancePanelIdle.Action.Game, MaintenancePanelIdle.Decide(dedicated: false, gameWork: false, instant: true));
        }
    }
}
