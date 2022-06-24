using System.Reflection;
using Havok;
using NLog;
using ParallelTasks;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class NpcPatches
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var MethodSpinOnce = typeof(MySpinWait).GetMethod
                (nameof(MySpinWait.SpinOnce), BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodSpinOnce).Prefixes.Add(
                typeof(DamagePatch).GetMethod(nameof(SpinOnce),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }


        private static bool SpinOnce()
        {
            return false;
        }

    }
}