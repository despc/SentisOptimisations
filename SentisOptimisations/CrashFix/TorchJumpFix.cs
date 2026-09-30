using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// Torch's patch jumps made one instruction, so that the runtime never stops a thread in the middle of one.
    ///
    /// Torch sends a patched method to its re-emitted copy with two instructions at the start of the original
    /// (<c>AssemblyMemory.WriteJump</c>: <c>mov rax, target; jmp rax</c>, 12 bytes). A thread the runtime stops for a
    /// collection right between them is, by the runtime's lights, 10 bytes into the original method's prologue: it
    /// unwinds the pushes that prologue makes by then (0x28 bytes and more) - pushes never made - and puts its hijack
    /// stub's address where it takes the return address to be: into a slot of the calling frame. The caller, run on
    /// before the runtime puts the slot back, works with a wrong local: a finally handler took its frame pointer from
    /// such a slot and wrote an enumerator's fields into clr.dll's code (the old server's crashes, cdb data breakpoint
    /// 29.09.2026 21:32), an enumerator's version read wrong threw "Collection was modified" from collections nobody
    /// changed.
    ///
    /// Here the jump is one instruction: <c>jmp rel32</c> to a trampoline near the method (<c>jmp [rip]</c> to the
    /// target), as Harmony does. The jumps Torch has already written (its own patches, those committed before this
    /// plugin's Init) are rewritten the same way, and the later ones are written so from the start.
    /// </summary>
    public static class TorchJumpFix
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
        private static readonly object Lock = new object();
        private static int _rewritten, _written, _fallbacks;

        public static void Install()
        {
            try
            {
                var memory = typeof(PatchManager).Assembly.GetType("Torch.Managers.PatchManager.AssemblyMemory", true);
                var writeJump = memory.GetMethod("WriteJump", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                                    new[] { typeof(long), typeof(long) }, null)
                                ?? throw new MissingMethodException(memory.FullName, "WriteJump");
                CrashFixPatch.harmony.Patch(writeJump, prefix: new HarmonyMethod(typeof(TorchJumpFix).GetMethod(nameof(WriteJumpPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                RewriteExisting();
                SentisOptimisationsPlugin.Log.Info($"TorchJumpFix: {_rewritten} patch jumps rewritten as one instruction; later ones written so");
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "TorchJumpFix could not be installed; Torch's two-instruction patch jumps stay");
            }
        }

        /// <summary>Torch's WriteJump, with the jump one instruction; the bytes it returns are the same 12 it read.</summary>
        private static bool WriteJumpPrefix(long memory, long jumpTarget, ref byte[] __result)
        {
            var old = new byte[12];
            Marshal.Copy(new IntPtr(memory), old, 0, 12);
            __result = old;
            lock (Lock)
            {
                WriteOneInstructionJump(memory, jumpTarget);
                _written++;
            }
            return false;
        }

        /// <summary>The jumps Torch wrote before the prefix above was there.</summary>
        private static void RewriteExisting()
        {
            var patterns = typeof(PatchManager).GetField("_rewritePatterns", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as IDictionary
                           ?? throw new MissingFieldException("PatchManager._rewritePatterns");
            var decorated = new List<object>();
            var enumerator = patterns.GetEnumerator();
            while (enumerator.MoveNext()) decorated.Add(enumerator.Entry.Value);
            foreach (var method in decorated)
            {
                var field = method.GetType().GetField("_revertAddress", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null) continue;
                var address = (long)field.GetValue(method);
                if (address == 0) continue;
                if (!IsTorchJump(address, out var target)) continue;
                lock (Lock)
                {
                    Unprotect(address, 12);
                    WriteOneInstructionJump(address, target);
                    _rewritten++;
                }
            }
        }

        /// <summary>mov rax, imm64 (48 B8 ..); jmp rax (FF E0)</summary>
        public static bool IsTorchJump(byte[] code, out long target)
        {
            target = 0;
            if (code.Length < 12 || code[0] != 0x48 || code[1] != 0xB8 || code[10] != 0xFF || code[11] != 0xE0) return false;
            target = BitConverter.ToInt64(code, 2);
            return true;
        }

        private static bool IsTorchJump(long address, out long target)
        {
            var code = new byte[12];
            Marshal.Copy(new IntPtr(address), code, 0, 12);
            return IsTorchJump(code, out target);
        }

        /// <summary>jmp rel32 (E9) to a trampoline within reach; with none to be had, jmp [rip] (FF 25) in place.</summary>
        private static void WriteOneInstructionJump(long memory, long target)
        {
            var trampoline = Trampoline(memory, target);
            if (trampoline != 0)
            {
                var code = new byte[5];
                code[0] = 0xE9;
                BitConverter.GetBytes((int)(trampoline - (memory + 5))).CopyTo(code, 1);
                Marshal.Copy(code, 0, new IntPtr(memory), 5);
            }
            else
            {
                // 14 bytes: the start of a method is aligned to 16, so a method shorter than that still has its padding
                Marshal.Copy(AbsoluteJump(target), 0, new IntPtr(memory), 14);
                _fallbacks++;
            }
            FlushInstructionCache(GetCurrentProcess(), new IntPtr(memory), new UIntPtr(14));
        }

        /// <summary>jmp qword ptr [rip+0]; the target after it.</summary>
        public static byte[] AbsoluteJump(long target)
        {
            var code = new byte[14];
            code[0] = 0xFF;
            code[1] = 0x25;
            BitConverter.GetBytes(target).CopyTo(code, 6);
            return code;
        }

        // ------------------------------------------------------------------ trampolines

        private const int BlockSize = 64 * 1024;
        private const int SlotSize = 16;
        private const long Reach = int.MaxValue - BlockSize;

        private sealed class Block
        {
            public long Start;
            public int Used;
        }

        private static readonly List<Block> Blocks = new List<Block>();

        /// <summary>A trampoline to <paramref name="target"/> within rel32 reach of <paramref name="from"/>; 0 when none can be had.</summary>
        private static long Trampoline(long from, long target)
        {
            Block block = null;
            foreach (var candidate in Blocks)
                if (candidate.Used + SlotSize <= BlockSize && Math.Abs(candidate.Start - from) < Reach) { block = candidate; break; }
            if (block == null)
            {
                var start = Allocate(from);
                if (start == 0) return 0;
                block = new Block { Start = start };
                Blocks.Add(block);
            }
            var slot = block.Start + block.Used;
            block.Used += SlotSize;
            Marshal.Copy(AbsoluteJump(target), 0, new IntPtr(slot), 14);
            FlushInstructionCache(GetCurrentProcess(), new IntPtr(slot), new UIntPtr(SlotSize));
            return slot;
        }

        /// <summary>64 KB of executable memory as near <paramref name="near"/> as can be had, within rel32 reach.</summary>
        private static long Allocate(long near)
        {
            const long granularity = 0x10000;
            var baseAddress = near & ~(granularity - 1);
            for (long step = 1; step * granularity < Reach; step++)
                foreach (var candidate in new[] { baseAddress - step * granularity, baseAddress + step * granularity })
                {
                    if (candidate <= 0) continue;
                    var got = VirtualAlloc(new IntPtr(candidate), new UIntPtr(BlockSize), MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
                    if (got != IntPtr.Zero)
                    {
                        if (Math.Abs(got.ToInt64() - near) < Reach) return got.ToInt64();
                        VirtualFree(got, UIntPtr.Zero, MEM_RELEASE);
                    }
                }
            return 0;
        }

        private static void Unprotect(long address, int length)
        {
            if (!VirtualProtect(new IntPtr(address), new UIntPtr((uint)length), PAGE_EXECUTE_READWRITE, out _))
                throw new System.ComponentModel.Win32Exception();
        }

        private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, PAGE_EXECUTE_READWRITE = 0x40;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

        [DllImport("kernel32.dll")]
        private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
    }
}
