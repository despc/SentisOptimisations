using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using SentisOptimisationsPlugin.Freezer;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class DiscordBridgePatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().
                SingleOrDefault(assembly => assembly.GetName().Name == "SEDiscordBridge");
            if (assembly == null)
            {
                Log.Warn("No discord bridge found, skip patch");
                return;
            }
            var type = assembly.GetType("SEDiscordBridge.DiscordBridge");
            if (type == null)
            {
                Log.Warn("No discord bridge found, skip patch");
                return;
            }
            var SendStatusM = type.GetMethod
                ("SendStatus", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (SendStatusM == null)
            {
                Log.Warn("No discord bridge found, skip patch");
                return;
            }
            ctx.GetPattern(SendStatusM).Prefixes.Add(
                typeof(DiscordBridgePatch).GetMethod(nameof(SendStatusPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
            Log.Warn("Patch discord bridge success");
        }

        private static void SendStatusPatched(ref string status)
        {
            try
            {
                if (!status.Contains("{cpu}"))
                {
                    return;
                }
                status = status.Replace("{cpu}", "" + FreezeLogic.GetAvgCpuLoad() + "%");
            }
            catch (Exception e)
            {
               //
            }
            
        }
    }
}