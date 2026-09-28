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
        public void A_client_always_runs_the_game_update()
        {
            Assert.Equal(MaintenancePanelIdle.Action.Game, MaintenancePanelIdle.Decide(dedicated: false, gameWork: false, instant: true));
        }
    }
}
