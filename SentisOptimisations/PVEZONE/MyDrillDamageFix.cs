using System.Linq;
using System.Reflection;
using NAPI;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisOptimisation.PveZone
{
    [PatchShim]
    internal static class MyDrillDamageFix
    {
        private static FieldInfo drillEntity;

        public static void Patch(PatchContext ctx)
        {
            drillEntity = typeof(MyDrillBase).easyField("m_drillEntity");
            ctx.Prefix(typeof(MyDrillBase), typeof(MyDrillDamageFix), nameof(TryDrillBlocks));
        }

        private static bool TryDrillBlocks(MyDrillBase __instance, ref bool __result)
        {
            if (!SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.PvEZoneEnabled)
            {
                return true;
            }

            if (drillEntity.GetValue(__instance) is MyHandDrill handDrill)
            {
                var myPlayer = MySession.Static.Players.GetOnlinePlayers().ToList().Find((MyPlayer b) => b.Identity.IdentityId == handDrill.OwnerIdentityId);

                if (myPlayer?.Character == null)
                    return true;

                var playerPosition = myPlayer.Character.PositionComp.GetPosition();
                if (PvECore.PveSphere.Contains(playerPosition) == ContainmentType.Contains)
                {
                    __result = false;
                    return false;
                }
            }
            if (drillEntity.GetValue(__instance) is MyShipDrill myShipDrill)
            {
                if (PvECore.EntitiesInZone.Contains(myShipDrill.CubeGrid.EntityId))
                {
                    __result = false;
                    return false;
                }

            }
            return true;
        }
    }
}
