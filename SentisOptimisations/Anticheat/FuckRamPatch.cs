using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using NLog;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI.Ingame;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class FuckRamPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("FuckRamPatch", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            
            var SetStorage = typeof(MyGridProgram).GetProperty(nameof(MyGridProgram.Storage)).GetSetMethod(true);
            var SetCustomData = typeof(MyTerminalBlock).GetProperty(nameof(MyTerminalBlock.CustomData)).GetSetMethod();
            var SetCustomNames = typeof(MyTerminalBlock).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(info => info.Name.Equals(nameof(MyTerminalBlock.SetCustomName)));
            ctx.GetPattern(SetStorage).Prefixes.Add(
                typeof(FuckRamPatch).GetMethod(nameof(SetValueLimitedPatchedStorage),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            ctx.GetPattern(SetCustomData).Prefixes.Add(
                typeof(FuckRamPatch).GetMethod(nameof(SetValueLimitedPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            foreach (var m in SetCustomNames)
            {
                ctx.GetPattern(m).Prefixes.Add(
                    typeof(FuckRamPatch).GetMethod(nameof(SetValueLimitedPatchedText),
                        BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            }
            
        }

        private static bool SetValueLimitedPatchedText(object text)
        {
        try
        {
            var value = "";
            if (text is StringBuilder)
            {
                value = text.ToString();
            }
            else
            {
                value = (string)text;
            }
            if (value.Length > 512)
            {
                Log.Error("DisplayName TOO LONG " + value.Substring(0, 500));
                return false;
            }
            return true;
        


            }
                catch (Exception __guard_e)
                {
                    Log.Error("SetValueLimitedPatchedText exception " + __guard_e);
                    return true;  // fall back to vanilla behavior
                }
        }
        
        private static bool SetValueLimitedPatchedStorage(String value)
        {
        try
        {
            if (value.Length > 100240)
            {
                Log.Error("Storage TOO LONG " + value.Substring(0, 500));
                return false;
            }
            return true;
        


            }
                catch (Exception __guard_e)
                {
                    Log.Error("SetValueLimitedPatchedStorage exception " + __guard_e);
                    return true;  // fall back to vanilla behavior
                }
        }
        
        private static bool SetValueLimitedPatched(String value)
        {
        try
        {
            if (value.Length > 100240)
            {
                Log.Error("CustomData TOO LONG " + value.Substring(0, 500));
                return false;
            }
            return true;
        


            }
                catch (Exception __guard_e)
                {
                    Log.Error("SetValueLimitedPatched exception " + __guard_e);
                    return true;  // fall back to vanilla behavior
                }
        }
    }
}