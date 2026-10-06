using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using VRage.Library.Compiler;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Whether a script's Save() does anything. A frozen grid's builder is made on a worker in a parallel world save
    /// (ParallelEntitySave), and the builder of a programmable block calls the script's Save(): a script that moves
    /// items, switches a connector or writes a screen there would do it off the game thread, next to the other grids'
    /// builders (physics, inventories and replication are not made for that). The template every script starts from
    /// has an empty Save(), and most scripts keep it or have none: those grids stay on the workers, a grid whose
    /// script's Save() has code is built on the game thread.
    /// "Does anything" is a call: all a script reaches of the game goes through one (a property is one too). The
    /// compiler's own counters in the body (IlInjector) are not the script's code. Pure logic, no game state.
    /// </summary>
    public static class ScriptSaveCode
    {
        private static readonly OpCode[] OneByte = new OpCode[256];
        private static readonly OpCode[] TwoByte = new OpCode[256];
        private static readonly ConditionalWeakTable<Type, object> ByProgram = new ConditionalWeakTable<Type, object>();

        static ScriptSaveCode()
        {
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!(field.GetValue(null) is OpCode op)) continue;
                var value = (ushort)op.Value;
                if (op.Size == 1) OneByte[value] = op;
                else TwoByte[value & 0xFF] = op;
            }
        }

        /// <summary>The same for a program's type, remembered (a script's type lives as long as its assembly).</summary>
        public static bool HasCode(Type program)
        {
            if (program == null) return false;
            return (bool)ByProgram.GetValue(program, type =>
            {
                MethodInfo save;
                try
                {
                    // (found the way the game finds it: MyGridProgram)
                    save = type.GetMethod("Save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                catch (AmbiguousMatchException)
                {
                    return true;
                }
                return save != null && HasCode(save);
            });
        }

        /// <summary>Whether the method calls anything but the compiler's counters. What cannot be read counts as code.</summary>
        public static bool HasCode(MethodInfo method)
        {
            try
            {
                var il = method?.GetMethodBody()?.GetILAsByteArray();
                if (il == null) return true;
                var typeArguments = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
                var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
                for (var at = 0; at < il.Length;)
                {
                    OpCode op;
                    if (il[at] == 0xFE)
                    {
                        if (at + 1 >= il.Length) return true;
                        op = TwoByte[il[at + 1]];
                        at += 2;
                    }
                    else op = OneByte[il[at++]];
                    if (op.Size == 0) return true;
                    switch (op.OperandType)
                    {
                        case OperandType.InlineNone:
                            break;
                        case OperandType.ShortInlineBrTarget:
                        case OperandType.ShortInlineI:
                        case OperandType.ShortInlineVar:
                            at += 1;
                            break;
                        case OperandType.InlineVar:
                            at += 2;
                            break;
                        case OperandType.InlineI8:
                        case OperandType.InlineR:
                            at += 8;
                            break;
                        case OperandType.InlineSwitch:
                            at += 4 + 4 * BitConverter.ToInt32(il, at);
                            break;
                        case OperandType.InlineSig:
                            return true;
                        case OperandType.InlineMethod:
                            // call, callvirt, newobj, ldftn, ldvirtftn, jmp
                            var called = method.Module.ResolveMethod(BitConverter.ToInt32(il, at), typeArguments, methodArguments);
                            if (called.DeclaringType != typeof(IlInjector)) return true;
                            at += 4;
                            break;
                        default:
                            at += 4;
                            break;
                    }
                }
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
