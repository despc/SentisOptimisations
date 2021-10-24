using System.Text;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;
using Torch.Commands;
using Torch.Commands.Permissions;
using Torch.Mod;
using Torch.Mod.Messages;
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin.AnomalyZone
{
    [Category("az")]
    public class AZCommands : CommandModule
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        
        [Command("score", ".", null)]
        [Permission(MyPromoteLevel.None)]
        public void AZScore()
        {
            var contextPlayer = (MyPlayer) Context.Player;
            ModCommunication.SendMessageTo(new DialogMessage("Anomaly zone research score", "", FormatScores()), contextPlayer.Client.SteamUserId);
        }
        
        private string FormatScores()
        {
            StringBuilder formatedScores = new StringBuilder();
            var configConfigAnomalyZone = SentisOptimisationsPlugin.Config.ConfigAnomalyZone;
            foreach (ConfigAnomalyZone configAnomalyZone in configConfigAnomalyZone)
            {
                var blockId = configAnomalyZone.BlockId;
                var entityById = MyEntities.GetEntityById(blockId);
                if (entityById != null)
                {
                    formatedScores.AppendLine(((MySafeZoneBlock) entityById).DisplayName);
                }
                
                foreach (var configAnomalyZonePointse in configAnomalyZone.Points)
                {
                    IMyFaction f = MySession.Static.Factions.TryGetFactionById(configAnomalyZonePointse.FactionId);
                    formatedScores.AppendLine($"[{f.Tag}] {f.Name}: {configAnomalyZonePointse.Points}");
                }
            }
            return formatedScores.ToString();
        }
        
        [Command("reset", ".", null)]
        [Permission(MyPromoteLevel.Moderator)]
        public void AZReset()
        {
            SentisOptimisationsPlugin.Config.ConfigAnomalyZone.Clear();
        }
        
    }
}