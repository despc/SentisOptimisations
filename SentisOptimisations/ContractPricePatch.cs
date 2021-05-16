using System;
using System.Reflection;
using NLog;
using Sandbox.Game.Contracts;
using Sandbox.Game.World.Generator;
using Torch.Managers.PatchManager;
using VRage.Game.ObjectBuilders.Components.Contracts;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public class ContractPricePatch
    {
        private static readonly double JUMP_DRIVE_DISTANCE = 2000000.0;
        private static readonly float AMOUNT_URANIUM_TO_RECHARGE = 3.75f;
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var MethodGetMoneyRewardForAcquisitionContract = typeof(MyContractTypeAcquisitionStrategy).GetMethod(
                "GetMoneyRewardForAcquisitionContract",
                BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodGetMoneyRewardForAcquisitionContract).Suffixes.Add(
                typeof(ContractPricePatch).GetMethod(nameof(PatchGetMoneyRewardForAcquisitionContract),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodGetMoneyReward_Escort = typeof(MyContractTypeEscortStrategy).GetMethod("GetMoneyReward_Escort",
                BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodGetMoneyReward_Escort).Suffixes.Add(
                typeof(ContractPricePatch).GetMethod(nameof(PatchGetMoneyReward_Escort),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodGetMoneyRewardForHaulingContract = typeof(MyContractTypeHaulingStrategy).GetMethod(
                "GetMoneyRewardForHaulingContract",
                BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodGetMoneyRewardForHaulingContract).Suffixes.Add(
                typeof(ContractPricePatch).GetMethod(nameof(PatchGetMoneyRewardForHaulingContract),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodGetMoneyRewardForRepairContract = typeof(MyContractTypeRepairStrategy).GetMethod(
                "GetMoneyRewardForRepairContract",
                BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodGetMoneyRewardForRepairContract).Suffixes.Add(
                typeof(ContractPricePatch).GetMethod(nameof(PatchGetMoneyRewardForRepairContract),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodMyContractFindInit = typeof(MyContractFind).GetMethod(
                nameof(MyContractFind.Init),BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodMyContractFindInit).Prefixes.Add(
                typeof(ContractPricePatch).GetMethod(nameof(PatchMyContractFindInit),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static void PatchGetMoneyRewardForAcquisitionContract(ref long __result, long baseRew, int amount)
        {
            try
            {
                __result = (long) (baseRew * Math.Pow(2.0, Math.Log10(amount)) * SentisOptimisationsPlugin.Config.ContractAcquisitionMultiplier);
            }
            catch (Exception e)
            {
                Log.Error("Exception in time PatchGetMoneyRewardForAcquisitionContract", e);
            }
        }

        private static void PatchGetMoneyReward_Escort(ref long __result, long baseRew, double distance)
        {
            try
            {
                __result = (long) (baseRew * Math.Pow(3.0, Math.Log10(distance)) * SentisOptimisationsPlugin.Config.ContractEscortMultiplier);
            }
            catch (Exception e)
            {
                Log.Error("Exception in time PatchGetMoneyReward_Escort", e);
            }
        }

        private static void PatchGetMoneyRewardForHaulingContract(ref long __result, long baseRew, double distance,
            int uraniumPrice)
        {
            try
            {
                double num1 = distance / JUMP_DRIVE_DISTANCE;
                double num2 = num1 * (uraniumPrice * (double) AMOUNT_URANIUM_TO_RECHARGE);
                __result = (long) ((baseRew + baseRew * num1 + num2) * SentisOptimisationsPlugin.Config.ContractHaulingtMultiplier);
            }
            catch (Exception e)
            {
                Log.Error("Exception in time PatchGetMoneyRewardForHaulingContract", e);
            }
        }

        private static void PatchGetMoneyRewardForRepairContract(ref long __result, long baseRew,
            double gridDistance,
            long gridPrice,
            float gridPriceToRewardcoef)
        {
            try
            {
                __result = (long)  ((baseRew * Math.Pow(2.0, Math.Log10(gridDistance)) +
                                   (long) (gridPriceToRewardcoef * (double) gridPrice)) * SentisOptimisationsPlugin.Config.ContractRepairMultiplier);
            }
            catch (Exception e)
            {
                Log.Error("Exception in time PatchGetMoneyRewardForHaulingContract", e);
            }
        }
        
        private static void PatchMyContractFindInit(ref MyObjectBuilder_Contract ob)
        {
            try
            {
                ob.RewardMoney = (long) (ob.RewardMoney * SentisOptimisationsPlugin.Config.ContractFindMultiplier);
            }
            catch (Exception e)
            {
                Log.Error("Exception in time PatchMyContractFindInit", e);
            }
        }
    }
}