using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog.Fluent;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SpaceEngineers.Game.World;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public class GasTankOptimisations
    {
        private static Dictionary<long, List<double>> _accumulatedTransfer = new Dictionary<long, List<double>>();
        public static void Patch(PatchContext ctx)
        {
            var MethodExecuteGasTransfer = typeof(MyGasTank).GetMethod
                ("ExecuteGasTransfer", BindingFlags.Instance | BindingFlags.NonPublic);


            ctx.GetPattern(MethodExecuteGasTransfer).Prefixes.Add(
                typeof(GasTankOptimisations).GetMethod(nameof(MethodExecuteGasTransferPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodGetAvailableRespawnPoints = typeof(MySpaceRespawnComponent).GetMethod
                (nameof(MySpaceRespawnComponent.GetRespawnShips), BindingFlags.Static | BindingFlags.Public);


            ctx.GetPattern(MethodGetAvailableRespawnPoints).Suffixes.Add(
                typeof(GasTankOptimisations).GetMethod(nameof(GetAvailableRespawnPointsPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            

        }


        private static void GetAvailableRespawnPointsPatched(MyPlanet planet, ref ClearToken<MyRespawnShipDefinition> __result)
        {
            if (!planet.Name.Contains("Earth"))
            {
                __result.List.Clear();
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
    }
}