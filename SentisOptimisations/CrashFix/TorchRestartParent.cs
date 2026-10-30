using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// Torch's restart (<c>!restart</c>, a scheduled restart, the one after a Torch update) brings the server back up.
    ///
    /// Torch 1.3.1.349 (commit 6cb977f7, 30.10.2026) looks at its parent process first thing in <c>Program.Main</c>
    /// (<c>IsRunningAsService</c>: <c>Process.GetProcessById</c> of the parent's id, from WMI). A parent gone makes it
    /// throw; <c>Main</c> catches it and logs it to an NLog not set up yet - nothing written - and the process ends
    /// with code 0 a second after its start. Torch restarts itself by starting a new Torch (<c>-waitforpid</c>) and
    /// killing its own process at once (<c>TorchServer.Restart</c>, its <c>DoRestart</c>): the new one finds its
    /// parent gone, every time. The stand, 30.10.2026: the restart after the update to 349 and <c>!restart 0</c> both
    /// left no server, the new process out in 1.0 s with code 0.
    ///
    /// Here the old process, having started the new one, waits until the new one has run <see cref="PastCheck"/> (past
    /// the look at its parent; it waits for the old one's end by itself then) or has ended, at most
    /// <see cref="MaxWait"/>, and only then kills itself. Put in only while Torch has the fault: <c>Program</c>'s
    /// <c>IsRunningAsService</c> calling <c>GetProcessById</c> with no catch round it - a Torch that catches it, or
    /// looks otherwise, gets nothing done here (written to the log at the start either way).
    /// </summary>
    [PatchShim]
    public static class TorchRestartParent
    {
        private static readonly TimeSpan PastCheck = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(10);

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("TorchRestartParent", ctx, c =>
        {
            var program = Type.GetType("Torch.Server.Program, Torch.Server");
            var server = Type.GetType("Torch.Server.TorchServer, Torch.Server");
            if (program == null || server == null)
            {
                SentisOptimisationsPlugin.Log.Info("TorchRestartParent: not a Torch server; nothing done");
                return;
            }
            var fault = Faulty(program);
            if (fault == null)
            {
                SentisOptimisationsPlugin.Log.Info("TorchRestartParent: Torch does not end a new process whose parent is gone; its restart is left as it is");
                return;
            }
            var restart = server.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                              .FirstOrDefault(m => m.Name.Contains("DoRestart"))
                          ?? throw new MissingMethodException("TorchServer.Restart's DoRestart");
            CrashFixPatch.harmony.Patch(restart, transpiler: new HarmonyMethod(typeof(TorchRestartParent).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic)));
            SentisOptimisationsPlugin.Log.Info($"TorchRestartParent: Torch ends a new process whose parent is gone ({fault}); its restart waits for the new process before ending");
        });

        /// <summary>The faulty look at the parent, if Torch has it: Program's IsRunningAsService with GetProcessById and no catch.</summary>
        private static string Faulty(Type program)
        {
            foreach (var method in program.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                         .Where(m => m.Name.Contains("IsRunningAsService")))
            {
                var body = method.GetMethodBody();
                if (body == null || body.ExceptionHandlingClauses.Any(x => x.Flags == ExceptionHandlingClauseOptions.Clause || x.Flags == ExceptionHandlingClauseOptions.Filter)) continue;
                var calls = PatchProcessor.GetOriginalInstructions(method).Select(i => i.operand).OfType<MethodBase>().Select(m => m.Name);
                if (calls.Contains("GetProcessById")) return method.Name;
            }
            return null;
        }

        // Process.Start(...) whose new process was dropped (pop): handed to Started instead
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var found = 0;
            for (var i = 0; i + 1 < list.Count; i++)
            {
                if (!(list[i].operand is MethodInfo called) || called.DeclaringType != typeof(Process) || called.Name != nameof(Process.Start) || list[i + 1].opcode != OpCodes.Pop) continue;
                var call = new CodeInstruction(OpCodes.Call, typeof(TorchRestartParent).GetMethod(nameof(Started), BindingFlags.Static | BindingFlags.NonPublic));
                call.labels.AddRange(list[i + 1].labels);
                call.blocks.AddRange(list[i + 1].blocks);
                list[i + 1] = call;
                found++;
            }
            if (found != 1)
            {
                SentisOptimisationsPlugin.Log.Error($"TorchRestartParent: {found} new processes started in Torch's restart, not 1; left as it is");
                return instructions;
            }
            return list;
        }

        /// <summary>The new Torch started: waited for until past its look at its parent, or ended.</summary>
        private static void Started(Process child)
        {
            try
            {
                if (child == null) return;
                var waited = Stopwatch.StartNew();
                while (waited.Elapsed < MaxWait)
                {
                    child.Refresh();
                    if (child.HasExited)
                    {
                        SentisOptimisationsPlugin.Log.Warn($"TorchRestartParent: the new Torch {child.Id} ended in {waited.Elapsed.TotalSeconds:0.0} s, code {child.ExitCode}");
                        return;
                    }
                    if (waited.Elapsed >= PastCheck) break;
                    Thread.Sleep(100);
                }
                SentisOptimisationsPlugin.Log.Info($"TorchRestartParent: the new Torch {child.Id} runs after {waited.Elapsed.TotalSeconds:0.0} s; this one ends");
                NLog.LogManager.Flush();
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn(e, "TorchRestartParent: waiting for the new Torch");
            }
            finally
            {
                child?.Dispose();
            }
        }
    }
}
