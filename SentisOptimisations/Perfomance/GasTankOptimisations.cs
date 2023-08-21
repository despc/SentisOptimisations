using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.World;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public class GasTankOptimisations
    {
        private static Dictionary<long, List<double>> _accumulatedTransfer = new();
        private static Dictionary<long, List<float>> _accumulatedTransferVent = new();
        public static void Patch(PatchContext ctx)
        {
            var MethodExecuteGasTransfer = typeof(MyGasTank).GetMethod
                ("ExecuteGasTransfer", BindingFlags.Instance | BindingFlags.NonPublic);


            ctx.GetPattern(MethodExecuteGasTransfer).Prefixes.Add(
                typeof(GasTankOptimisations).GetMethod(nameof(MethodExecuteGasTransferPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodExecuteGasTransferVent = typeof(MyAirVent).GetMethod
                ("Transfer", BindingFlags.Instance | BindingFlags.NonPublic);


            ctx.GetPattern(MethodExecuteGasTransferVent).Prefixes.Add(
                typeof(GasTankOptimisations).GetMethod(nameof(MethodExecuteGasTransferPatchedVent),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

        }


        private static bool MethodExecuteGasTransferPatched(MyGasTank __instance, ref double totalTransfer)
        {
            if (!SentisOptimisationsPlugin.Config.GasTankOptimisation)
            {
                return true;
            }
            if (totalTransfer == 0.0)
            {
                return true;
            }
            List<double> accumulatedTransfers;
            _accumulatedTransfer.TryGetValue(__instance.EntityId, out accumulatedTransfers);
            if (accumulatedTransfers == null)
            {
                accumulatedTransfers = new List<double>();
                _accumulatedTransfer[__instance.EntityId] = accumulatedTransfers;
            }
            if (accumulatedTransfers.Count >= 30)
            {
                double sum = accumulatedTransfers.Sum();
                totalTransfer = sum;
                accumulatedTransfers.Clear();
                return true;
            }
            accumulatedTransfers.Add(totalTransfer);
            return false;
        }
        
        private static bool MethodExecuteGasTransferPatchedVent(MyAirVent __instance, ref float transferAmount)
        {
            if (!SentisOptimisationsPlugin.Config.GasTankOptimisation)
            {
                return true;
            }
            if (transferAmount == 0.0)
            {
                return true;
            }
            List<float> accumulatedTransfers;
            _accumulatedTransferVent.TryGetValue(__instance.EntityId, out accumulatedTransfers);
            if (accumulatedTransfers == null)
            {
                accumulatedTransfers = new List<float>();
                _accumulatedTransferVent[__instance.EntityId] = accumulatedTransfers;
            }
            if (accumulatedTransfers.Count >= 30)
            {
                float sum = accumulatedTransfers.Sum();
                transferAmount = sum;
                accumulatedTransfers.Clear();
                return true;
            }
            accumulatedTransfers.Add(transferAmount);
            return false;
        }
    }
}