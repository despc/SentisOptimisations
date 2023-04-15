using System;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using VRage;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class ParallelPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static readonly Random r = new Random();

        public static void Patch(PatchContext ctx)
        {
            var ThrustDamageAsync = typeof(MyThrust).GetMethod
                ("ThrustDamageAsync", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(ThrustDamageAsync).Prefixes.Add(
                typeof(ParallelPatch).GetMethod(nameof(ThrustDamageAsyncPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodAllocateId = typeof(MyEntityIdentifier).GetMethod
                (nameof(MyEntityIdentifier.AllocateId), BindingFlags.Static | BindingFlags.Public);
        }

        private static bool ThrustDamageAsyncPatched(MyThrust __instance)
        {
            if (r.NextDouble() < 0.05)
            {
                return true;
            }

            return false;
        }
    }
}