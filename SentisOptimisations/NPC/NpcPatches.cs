using System.Collections.Generic;
using System.Reflection;
using NLog;
using ParallelTasks;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class NpcPatches
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var MethodCheckLimitsAndNotify = typeof(MySession).GetMethod
                (nameof(MySession.CheckLimitsAndNotify), BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(MethodCheckLimitsAndNotify).Prefixes.Add(
                typeof(NpcPatches).GetMethod(nameof(CheckLimitsAndNotifyPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
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