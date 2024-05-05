using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NAPI;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class GasTankOptimisations
    {
        private static ConcurrentDictionary<MyGasTank, List<double>> _accumulatedTransfer = new();
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

        public static void UpdateTankRemains()
        {
            foreach (var at in _accumulatedTransfer)
            {
                var tank = at.Key;
                if (tank == null)
                {
                    continue;
                }
                var accumulatedTransfers = at.Value;
                if (accumulatedTransfers.Count < 30)
                {
                    var countToAdd = 30 - accumulatedTransfers.Count;
                    for (int i = 0; i < countToAdd; i++)
                    {
                        accumulatedTransfers.Add(0);
                    }

                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    {
                        try
                        {
                            tank.easyCallMethod("ExecuteGasTransfer", new object[] { accumulatedTransfers.Sum() });
                        }
                        catch (Exception e)
                        {
                            //
                        }
                        
                    });
                    
                }
            }
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
            _accumulatedTransfer.TryGetValue(__instance, out accumulatedTransfers);
            if (accumulatedTransfers == null)
            {
                accumulatedTransfers = new List<double>();
                _accumulatedTransfer[__instance] = accumulatedTransfers;
            }
            if (accumulatedTransfers.Count >= 30)
            {
                double sum = accumulatedTransfers.Sum();
                totalTransfer = sum;
                accumulatedTransfers.Clear();
                _accumulatedTransfer.Remove(__instance);
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