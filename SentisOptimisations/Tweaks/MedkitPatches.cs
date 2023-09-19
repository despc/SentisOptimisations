using System.Collections.Generic;
using System.Reflection;
using NAPI;
using NLog;
using Sandbox;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineers.Game.EntityComponents.GameLogic;
using Torch.Managers.PatchManager;
using VRage.ModAPI;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class MedkitPatches
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var MethodProvideSupport = typeof(MyLifeSupportingComponent).GetMethod
                (nameof(MyLifeSupportingComponent.ProvideSupport), BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodProvideSupport).Prefixes.Add(
                typeof(MedkitPatches).GetMethod(nameof(ProvideSupportPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
        }
        
        private static bool ProvideSupportPatched(MyLifeSupportingComponent __instance, MyCharacter user)
        {
            if (!__instance.Entity.IsWorking)
                return false;
            bool flag = false;
            var character = __instance.User;
            if (character == null)
            {
                character = user;
                var propertyInfo = typeof(MyLifeSupportingComponent).GetProperty("User",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                propertyInfo.SetValue(__instance, user);
                // __instance.User = user;
                if (__instance.Entity.RefuelAllowed)
                {
                    user.SuitBattery.ResourceSink.TemporaryConnectedEntity = (IMyEntity) __instance.Entity;
                    user.SuitBattery.RechargeMultiplier = 1;
                    __instance.RechargeSocket.PlugIn(user.SuitBattery.ResourceSink);

                    var characterInventory = character.GetInventoryBase();
                    foreach (var item in characterInventory.GetItems())
                    {
                        if (item.Content is MyObjectBuilder_GasContainerObject)
                        {
                            ((MyObjectBuilder_GasContainerObject) item.Content).GasLevel = 1.02f;
                            characterInventory.OnContentsChanged();
                        }
                    }
                    PlayerSuitRechargeEvent playerSuitRecharging = MyVisualScriptLogicProvider.PlayerSuitRecharging;
                    if (playerSuitRecharging != null)
                        playerSuitRecharging(character.GetPlayerIdentityId(), __instance.Entity.BlockType);
                }
            }
            __instance.easySetField("m_lastTimeUsed", MySandboxGame.TotalGamePlayTimeInMilliseconds);
            if (character.StatComp != null && __instance.Entity.HealingAllowed)
            {
                character.StatComp.DoAction("GenericHeal");
                flag = true;
                PlayerHealthRechargeEvent healthRecharging = MyVisualScriptLogicProvider.PlayerHealthRecharging;
                if (healthRecharging != null)
                {
                    float num = character.StatComp.Health != null ? character.StatComp.Health.Value : 0.0f;
                    healthRecharging(character.GetPlayerIdentityId(), __instance.Entity.BlockType, num);
                }
            }

            return false;
        }
        
        private static bool CheckLimitsAndNotifyPatch(long ownerID,
            string blockName,
            int pcuToBuild,
            int blocksToBuild,
            int blocksCount,
            Dictionary<string, int> blocksPerType, ref bool __result)
        {
            if (MySession.Static.Players.IdentityIsNpc(ownerID))
            {
                __result = true;
                return false;
            }
            return true;
        }
    }
}