using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities.Blocks;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class GasTankOptimisations
    {
        private static ConcurrentDictionary<MyGasTank, List<double>> _accumulatedTransfer = new();
        private static ConcurrentDictionary<long, List<float>> _accumulatedTransferVent = new();
        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("GasTankOptimisations", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
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
            var accumulatedTransfers = _accumulatedTransfer.GetOrAdd(__instance, _ => new List<double>());
            lock (accumulatedTransfers)
            {
                if (accumulatedTransfers.Count >= 30)
                {
                    // flush = everything accumulated PLUS the transfer being made right now
                    totalTransfer = accumulatedTransfers.Sum() + totalTransfer;
                    accumulatedTransfers.Clear();
                    _accumulatedTransfer.TryRemove(__instance, out _);
                    return true;
                }
                accumulatedTransfers.Add(totalTransfer);
                return false;
            }
        }

        /// <summary>Drop per-entity state when an entity is destroyed (called from EntitiesObserver).</summary>
        public static void CleanupEntity(VRage.Game.Entity.MyEntity entity)
        {
            if (entity is MyGasTank tank)
            {
                _accumulatedTransfer.TryRemove(tank, out _);
            }
            else if (entity is MyAirVent vent)
            {
                List<float> dropped;
                _accumulatedTransferVent.TryRemove(vent.EntityId, out dropped);
            }
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
            var accumulatedTransfers = _accumulatedTransferVent.GetOrAdd(__instance.EntityId, _ => new List<float>());
            lock (accumulatedTransfers)
            {
                if (accumulatedTransfers.Count >= 30)
                {
                    // flush = everything accumulated PLUS the transfer being made right now
                    transferAmount = accumulatedTransfers.Sum() + transferAmount;
                    accumulatedTransfers.Clear();
                    List<float> dropped;
                    _accumulatedTransferVent.TryRemove(__instance.EntityId, out dropped);
                    return true;
                }
                accumulatedTransfers.Add(transferAmount);
                return false;
            }
        }
    }
}