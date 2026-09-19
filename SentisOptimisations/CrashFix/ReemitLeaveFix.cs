using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using NLog;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// Undoes a Torch patch manager bug that silently changes the control flow of patched game
    /// methods - of any plugin.
    ///
    /// Any patch, even a prefix, makes Torch re-emit the whole original body. When a <c>leave</c> is
    /// directly followed by the start of a finally, catch or fault block or by the end of the
    /// exception block, Torch drops it and relies on the <c>leave</c> the ILGenerator emits for the
    /// block, which always goes to the end of that block. The C# compiler, however, points a
    /// <c>leave</c> straight at its final destination whenever the end of the block is only a jump to
    /// somewhere else, so after the re-emit such a jump lands at the end of the block instead and runs
    /// code the original never runs there. Found on this server:
    /// <list type="bullet">
    /// <item>MyGridConveyorSystem.PullItems: an item that CanTransfer refused falls through into the
    /// else branch and is added to the items to transfer, so conveyors move what they must not
    /// (patched by PullBackoff);</item>
    /// <item>MyGridPhysics.PerformDeformation: the end of the loop over this frame's contact points
    /// falls into the "first collision this frame" branch, which clears that cache and the slowdown
    /// flag on every further contact in the frame (patched by the damage patches);</item>
    /// <item>MySessionComponentContractSystem.UpdateAfterSimulation: the end of a loop falls into the
    /// body of <c>while (queue.Count &gt; 0) queue.Dequeue()</c> and throws on an empty queue.</item>
    /// </list>
    /// Once every plugin has patched, each re-emitted method is checked, and where a dropped leave
    /// would land somewhere else than its target a transpiler puts a nop after it: Torch then emits
    /// the leave itself, with its own target.
    /// </summary>
    public static class ReemitLeaveFix
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>Methods the fix was applied to, as "Type.Method".</summary>
        public static readonly List<string> Fixed = new List<string>();

        /// <summary>Leaves kept by the transpiler since start (it runs on every re-emit of a fixed method).</summary>
        public static long LeavesKept;

        public static readonly MethodInfo KeepLeavesMethod =
            typeof(ReemitLeaveFix).GetMethod(nameof(KeepLeaves), BindingFlags.Static | BindingFlags.Public);

        /// <summary>Checks every re-emitted method and fixes those the bug redirects. Call once all plugins have patched.</summary>
        public static void Apply(PatchManager patchManager)
        {
            if (patchManager == null) return;
            var patterns = typeof(PatchManager).GetField("_rewritePatterns", BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null) as IDictionary;
            if (patterns == null)
            {
                Log.Warn("Torch re-emit leave fix: PatchManager._rewritePatterns not found, nothing checked");
                return;
            }

            var affected = new List<MethodBase>();
            foreach (var entry in Entries(patterns))
            {
                var method = (MethodBase)entry.Key;
                if (HasFix(entry.Value)) continue;
                try
                {
                    if (Redirects(method)) affected.Add(method);
                }
                catch (Exception e)
                {
                    Log.Warn("Torch re-emit leave fix: could not read " + method.DeclaringType?.FullName + "." + method.Name + ": " + e.Message);
                }
            }
            if (affected.Count == 0) return;

            var context = patchManager.AcquireContext();
            foreach (var method in affected)
            {
                context.GetPattern(method).Transpilers.Add(KeepLeavesMethod);
                Fixed.Add(method.DeclaringType?.FullName + "." + method.Name);
            }
            patchManager.Commit();
            Log.Info("Torch re-emit leave fix applied to " + affected.Count + " patched methods: " + string.Join(", ", Fixed));
        }

        /// <summary>Transpiler: a nop after a leave Torch would drop makes it emit that leave, with its target.</summary>
        public static IEnumerable<MsilInstruction> KeepLeaves(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                yield return list[i];
                if (!Dropped(list, i)) continue;
                LeavesKept++;
                yield return new MsilInstruction(OpCodes.Nop);
            }
        }

        // The non-generic enumerator: LINQ over an IDictionary would go through the generic one,
        // which yields KeyValuePairs, not DictionaryEntries.
        private static List<DictionaryEntry> Entries(IDictionary dictionary)
        {
            var entries = new List<DictionaryEntry>();
            var enumerator = dictionary.GetEnumerator();
            while (enumerator.MoveNext()) entries.Add(enumerator.Entry);
            return entries;
        }

        private static bool HasFix(object decoratedMethod)
        {
            var transpilers = decoratedMethod.GetType().GetProperty("Transpilers")?.GetValue(decoratedMethod) as IEnumerable<MethodInfo>;
            return transpilers != null && transpilers.Any(t => t.Name == nameof(KeepLeaves));
        }

        /// <summary>Whether the re-emit sends some dropped leave of the method somewhere else than its target.</summary>
        public static bool Redirects(MethodBase method)
        {
            var list = PatchUtilities.ReadInstructions(method).ToList();
            var labelAt = new Dictionary<MsilLabel, int>();
            for (var i = 0; i < list.Count; i++)
                foreach (var label in list[i].Labels)
                    labelAt[label] = i;

            // Exception blocks: which one each instruction is in, the instruction that ends each (the
            // first one after it), and where a handler of a block starts - the ILGenerator emits its
            // own leave to the end of that block right before it.
            var open = new Stack<int>();
            var blockOf = new int[list.Count];
            var blockEnd = new Dictionary<int, int>();
            var handlerStart = new Dictionary<int, int>();
            var nextBlock = 0;
            for (var i = 0; i < list.Count; i++)
            {
                foreach (var op in list[i].TryCatchOperations)
                {
                    if (op.Type == MsilTryCatchOperationType.BeginExceptionBlock) open.Push(nextBlock++);
                    else if (op.Type == MsilTryCatchOperationType.EndExceptionBlock)
                    {
                        if (open.Count > 0) blockEnd[open.Pop()] = i;
                    }
                    else if (open.Count > 0) handlerStart[i] = open.Peek();
                }
                blockOf[i] = open.Count > 0 ? open.Peek() : -1;
            }

            // Where control really ends up, following the leaves on the way.
            int Resolve(int at)
            {
                for (var guard = 0; guard < 32; guard++)
                {
                    if (handlerStart.TryGetValue(at, out var block) && blockEnd.TryGetValue(block, out var end))
                    {
                        at = end;
                        continue;
                    }
                    if (!IsLeave(list[at])) break;
                    if (Dropped(list, at))
                    {
                        if (blockOf[at] < 0 || !blockEnd.TryGetValue(blockOf[at], out var droppedEnd)) break;
                        at = droppedEnd;
                    }
                    else if (list[at].Operand is MsilOperandBrTarget target && labelAt.TryGetValue(target.Target, out var next))
                        at = next;
                    else break;
                }
                return at;
            }

            for (var i = 0; i + 1 < list.Count; i++)
            {
                if (!Dropped(list, i)) continue;
                if (!(list[i].Operand is MsilOperandBrTarget target) || !labelAt.TryGetValue(target.Target, out var targetIndex)) continue;
                if (blockOf[i] < 0 || !blockEnd.TryGetValue(blockOf[i], out var end)) continue;
                if (Resolve(targetIndex) != Resolve(end)) return true;
            }
            return false;
        }

        private static bool Dropped(List<MsilInstruction> list, int i) =>
            IsLeave(list[i]) && i + 1 < list.Count && list[i + 1].TryCatchOperations.Any(op =>
                op.Type == MsilTryCatchOperationType.EndExceptionBlock ||
                op.Type == MsilTryCatchOperationType.BeginClauseBlock ||
                op.Type == MsilTryCatchOperationType.BeginFaultBlock ||
                op.Type == MsilTryCatchOperationType.BeginFinallyBlock);

        private static bool IsLeave(MsilInstruction instruction) =>
            instruction.OpCode == OpCodes.Leave || instruction.OpCode == OpCodes.Leave_S;
    }
}
