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

        public static void Patch(PatchContext ctx)
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
                Log.Error("DisplayName TOO LONG " + value);
                return false;
            }
            return true;
        }
        
        private static bool SetValueLimitedPatchedStorage(String value)
        {
            if (value.Length > 100240)
            {
                Log.Error("Storage TOO LONG " + value);
                return false;
            }
            return true;
        }
        
        private static bool SetValueLimitedPatched(String value)
        {
            if (value.Length > 100240)
            {
                Log.Error("CustomData TOO LONG " + value);
                return false;
            }
            return true;
        }
    }
}