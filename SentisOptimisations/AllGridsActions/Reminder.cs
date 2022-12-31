using System;
using NLog;
using Sandbox.Game;
using Sandbox.Game.Entities;
using SentisOptimisations;

namespace SentisOptimisationsPlugin.AllGridsActions
{
    public class Reminder
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public void RemindOffline(MyCubeGrid grid)
        {
            try
            {
                if (!SentisOptimisationsPlugin.Config.OfflineProtectionReminder)
                {
                    return;
                }

                foreach (var myCubeBlock in grid.GetFatBlocks())
                {
                    var idSubtypeName = myCubeBlock.BlockDefinition.Id.SubtypeName;
                    if (idSubtypeName == null)
                    {
                        continue;
                    }

                    if (idSubtypeName.Equals("Offline"))
                    {
                        foreach (var gridBigOwner in grid.BigOwners)
                        {
                            var factionOfPlayer = FactionUtils.GetFactionOfPlayer(gridBigOwner);
                            if (factionOfPlayer != null)
                            {
                                return;
                            }

                            ChatUtils.SendTo(gridBigOwner, "Offline protection not active!");
                            ChatUtils.SendTo(gridBigOwner,
                                "Must join or create a faction to use offline protection!");
                            MyVisualScriptLogicProvider.ShowNotification("ОФФЛАЙН ЗАЩИТА НЕ АКТИВНА!", 5000,
                                "Red", gridBigOwner);
                            MyVisualScriptLogicProvider.ShowNotification(
                                "Необходимо вступить или создать фракцию для использования оффлайн защиты", 5000,
                                "Red", gridBigOwner);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("Reminder exception ", e);
            }
        }
    }
}