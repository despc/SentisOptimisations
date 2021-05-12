using System;
using System.Linq;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;

namespace NoScrapPatch
{
    [PatchShim]
    public static class NoScrapPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var methods = typeof(MyFloatingObjects).GetMethods(
                BindingFlags.Static | BindingFlags.Public);

            var spawns = methods.ToList().FindAll(info => info.Name.Equals("Spawn"));

            ctx.GetPattern(spawns[1]).Prefixes.Add(
                typeof(NoScrapPatch).GetMethod(nameof(SpawnPatched1),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool SpawnPatched1(
            MyPhysicalInventoryItem item)
        {
            try
            {
                if (item.GetObjectBuilder() == null || item.GetObjectBuilder().Content == null
                                                    || item.GetObjectBuilder().Content.SubtypeName == null
                )
                {
                    return true;
                }

                if (item.GetObjectBuilder() != null && item.GetObjectBuilder().Content != null
                                                    && item.GetObjectBuilder().Content.SubtypeName != null
                                                    && item.GetObjectBuilder().Content.SubtypeName.Equals("Scrap"))
                {
                    return false;
                }
            }
            catch (Exception e)
            {
                Log.Error("Exception in time attempt spawn object patch", e);
            }

            return true;
        }
    }
}