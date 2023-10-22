using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using SentisOptimisations;

namespace SentisOptimisationsPlugin;

public class CommunicationUtils
{
    public static void SyncConvert(MyCubeGrid grid, bool isStatic)
    {
        ConvertSyncRequest response = new ConvertSyncRequest();
        response.IsStatic = isStatic;
        response.GridEntityId = grid.EntityId;
        foreach (var p in PlayerUtils.GetAllPlayers())
        {
            if (p.IsBot)
            {
                continue;
            }

            if (p.Character == null)
            {
                continue;
            }
            Communication.SendToClient(MessageType.SyncConvert,
                MyAPIGateway.Utilities.SerializeToBinary(response),p.SteamUserId);
        }

    }
}