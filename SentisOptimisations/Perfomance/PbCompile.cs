using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NLog;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage;
using VRage.FileSystem;
using VRage.Scripting;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// A programmable block's script compiled off the game thread, and compiled to machine code before its first run.
    ///
    /// Vanilla <c>MyProgrammableBlock.Compile</c> starts the compiler's task and waits for it on the game thread
    /// (<c>CompileIngameScriptAsync(...).Result</c>): Isy's Inventory Manager, 100 000 characters, held the game thread 1.5 s
    /// (dotTrace Timeline, stand, 04.10.2026) - every paste, switch-on or recompile of a big script froze the server.
    /// And the script's methods were then compiled to machine code at their first calls, inside its runs: 409 ms of JIT on
    /// the game thread, in runs of 4-26 ms at its start and in the first round of each of its tasks - what PBFix took for a
    /// heavy script and punished.
    ///
    /// Here the compile runs on a worker, and right after it the methods and constructors of the script's assembly are
    /// prepared (<see cref="RuntimeHelpers.PrepareMethod(RuntimeMethodHandle)"/>: compiled to machine code). Preparing can
    /// run a type's static initialization, and the script's code must not run there (see <see cref="PrepareTypes"/> and
    /// <see cref="RunsScriptCodeAhead"/>): such types, or such an assembly, are left for their first calls. On the
    /// game thread then, as vanilla does after its compile: the assembly and the compiler's messages set, and the script's
    /// instance made (with its constructor's run) - unless the block is gone or compiled again meanwhile. Until then the
    /// block has no assembly: a run asked for meanwhile is answered "no assembly", as before a first compile.
    /// </summary>
    public static class PbCompile
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo ScriptComponentField = typeof(MyProgrammableBlock).GetField("m_scriptComponent", Any);
        private static readonly FieldInfo TerminationReasonField = typeof(MyProgrammableBlock).GetField("m_terminationReason", Any);
        private static readonly FieldInfo CompilerErrorsField = typeof(MyProgrammableBlock).GetField("m_compilerErrors", Any);
        private static readonly FieldInfo InstanceField = typeof(MyProgrammableBlock).GetField("m_instance", Any);
        private static readonly PropertyInfo CurrentAssemblyProperty = typeof(MyProgrammableBlock).GetProperty("CurrentAssembly", Any);
        private static readonly MethodInfo CreateInstanceMethod = typeof(MyProgrammableBlock).GetMethod("CreateInstance", Any);
        private static readonly MethodInfo SetDetailedInfoMethod = typeof(MyProgrammableBlock).GetMethod("SetDetailedInfo", Any, null, new[] { typeof(string) }, null);
        private static readonly MethodInfo GetAssemblyNameMethod = typeof(MyProgrammableBlock).GetMethod("GetAssemblyName", Any);

        /// <summary>The latest compile started for each block: a compile that finishes after a newer one began is dropped.</summary>
        private static readonly ConcurrentDictionary<long, int> Latest = new ConcurrentDictionary<long, int>();
        private static int _counter;

        /// <summary>Throws when the game moved something this needs: the patch is then left out and vanilla compiles.</summary>
        public static void Bind()
        {
            if (ScriptComponentField == null || TerminationReasonField == null || CompilerErrorsField == null || InstanceField == null ||
                CurrentAssemblyProperty?.SetMethod == null || CreateInstanceMethod == null || SetDetailedInfoMethod == null || GetAssemblyNameMethod == null)
                throw new MissingMemberException("PbCompile: a member of MyProgrammableBlock could not be bound");
        }

        /// <summary>
        /// Vanilla's Compile, the compile itself on a worker. False when it is left to vanilla (not the server, not the game
        /// thread).
        /// </summary>
        public static bool Start(MyProgrammableBlock pb, string program, string storage, bool instantiate)
        {
            if (!Sandbox.Game.Multiplayer.Sync.IsServer || MySandboxGame.Static?.UpdateThread != System.Threading.Thread.CurrentThread) return false;

            // what vanilla does before it compiles
            if (ScriptComponentField.GetValue(pb) is Sandbox.Game.EntityComponents.MyIngameScriptComponent component)
            {
                component.NeedsUpdate = VRage.ModAPI.MyEntityUpdateEnum.NONE;
                component.UpdateFrequency = Sandbox.ModAPI.Ingame.UpdateFrequency.None;
            }
            if (!MySession.Static.EnableIngameScripts || pb.CubeGrid.IsPreview || !pb.CubeGrid.CreatePhysics) return true;
            TerminationReasonField.SetValue(pb, MyProgrammableBlock.ScriptTerminationReason.None);

            var generation = System.Threading.Interlocked.Increment(ref _counter);
            Latest[pb.EntityId] = generation;
            var assemblyName = Path.Combine(MyFileSystem.UserDataPath, (string)GetAssemblyNameMethod.Invoke(pb, null));
            var friendlyName = "PB: " + pb.DisplayName + " (" + pb.EntityId + ")";
            var trackMemory = Sandbox.Engine.Utils.MyFakes.ENABLE_PROGRAMMABLE_BLOCK_MEMORY_LIMIT;

            Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                Assembly assembly = null;
                List<string> messages = null;
                Exception failure = null;
                double compileMs = 0;
                var prepared = 0;
                try
                {
                    assembly = MyVRage.Platform.Scripting.CompileIngameScriptAsync(assemblyName, program, out var diagnostics, friendlyName,
                        "Program", "MyGridProgram", trackMemory).Result;
                    compileMs = watch.Elapsed.TotalMilliseconds;
                    messages = diagnostics.Select(m => m.Text).ToList();
                    if (assembly != null) prepared = Prepare(assembly);
                }
                catch (Exception e)
                {
                    failure = e is AggregateException aggregate ? aggregate.GetBaseException() : e;
                }
                var prepareMs = watch.Elapsed.TotalMilliseconds - compileMs;
                MyAPIGateway.Utilities.InvokeOnGameThread(() => Finish(pb, generation, assembly, messages, failure, storage, instantiate, compileMs, prepareMs, prepared));
            });
            return true;
        }

        /// <summary>Back on the game thread: as vanilla does after its compile.</summary>
        private static void Finish(MyProgrammableBlock pb, int generation, Assembly assembly, List<string> messages, Exception failure,
            string storage, bool instantiate, double compileMs, double prepareMs, int prepared)
        {
            try
            {
                if (!Latest.TryGetValue(pb.EntityId, out var latest) || latest != generation) return;   // compiled again meanwhile
                Latest.TryRemove(pb.EntityId, out _);
                if (pb.MarkedForClose || pb.Closed) return;
                if (failure != null)
                {
                    SetDetailedInfoMethod.Invoke(pb, new object[] { MyTexts.GetString(Sandbox.Game.Localization.MySpaceTexts.ProgrammableBlock_Exception_ExceptionCaught) + failure.Message });
                    return;
                }
                CurrentAssemblyProperty.SetValue(pb, assembly);
                var errors = (List<string>)CompilerErrorsField.GetValue(pb);
                errors.Clear();
                if (messages != null) errors.AddRange(messages);
                // (also when the compile came from the block's first update, which wanted the instance made right after it -
                // vanilla made it there, with the assembly it had just waited for)
                if (instantiate || InstanceField.GetValue(pb) == null)
                    CreateInstanceMethod.Invoke(pb, new object[] { assembly, errors, storage });
                if (global::SentisOptimisations.DiagLog.On)
                    Log.Info($"PbCompile: {pb.CustomName} on {pb.CubeGrid.DisplayName}: compiled in {compileMs:0} ms, {prepared} methods to machine code in {prepareMs:0} ms, off the game thread");
            }
            catch (Exception e)
            {
                Log.Error(e, "PbCompile: finishing the compile of " + pb.CustomName + " failed");
            }
        }

        /// <summary>
        /// Every method and constructor of the assembly compiled to machine code, none run: what the script's runs would
        /// otherwise do at their first calls. The whole assembly is left when the JIT could run a script's code ahead while
        /// preparing it (<see cref="RunsScriptCodeAhead"/>). The count prepared.
        /// </summary>
        public static int Prepare(Assembly assembly)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            if (types.Any(RunsScriptCodeAhead)) return 0;
            return PrepareTypes(types);
        }

        /// <summary>
        /// The methods and constructors of the types compiled to machine code. Generic definitions are left (they need
        /// their type arguments), and so are the script's types with a static constructor of their own and those whose
        /// field initializers run the script's code (preparing them would run it). The count prepared.
        /// </summary>
        public static int PrepareTypes(IEnumerable<Type> types)
        {
            var count = 0;
            foreach (var type in types)
            {
                if (type.ContainsGenericParameters) continue;
                // PrepareMethod runs the type's initializer: here, off the game thread and outside a run, where the
                // instruction counter the game put into a static constructor's body throws - and a type initializer that
                // threw throws for good ("The type initializer for 'Settings' threw an exception", 10.10.2026). Such a
                // type's methods are left for their first calls, as before. Field initializers alone (beforefieldinit)
                // have no counter in them - the game rewrites constructors and methods, not field initializers - so a type
                // with only those is prepared unless they call the script's code: the script's Program with any
                // "static readonly string[] ..." was left whole, its Main and all, for the game thread. The compiler's own
                // types (the lambdas' cache "<>c") are among them.
                if (type.TypeInitializer != null && ((type.Attributes & TypeAttributes.BeforeFieldInit) == 0 || RunsScriptCodeAhead(type))) continue;
                const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var method in type.GetMethods(declared))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters) continue;
                    try { RuntimeHelpers.PrepareMethod(method.MethodHandle); count++; }
                    catch (Exception) { /* one that cannot be prepared is compiled at its first call, as before */ }
                }
                foreach (var constructor in type.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try { RuntimeHelpers.PrepareMethod(constructor.MethodHandle); count++; }
                    catch (Exception) { }
                }
            }
            return count;
        }

        /// <summary>
        /// Whether the JIT could run the script's own code ahead, off the game thread, while preparing a method that reads
        /// this type's statics. A type without a static constructor of its own (beforefieldinit: only field initializers) may
        /// have its initializer run by the optimizing JIT as it compiles such a method - the game compiles scripts optimized -
        /// and skipping the type itself does not prevent that. Harmless while the initializer only makes the framework's or
        /// the game's objects (<c>new List&lt;int&gt;()</c>, <c>new MyIni()</c>); with the script's code in it
        /// (<c>new MyMinItem(50)</c>, a script's method, another script type's statics) it throws there as a static
        /// constructor does. A type with a static constructor of its own is never run ahead by the JIT, only at its first use.
        /// </summary>
        public static bool RunsScriptCodeAhead(Type type)
        {
            if ((type.Attributes & TypeAttributes.BeforeFieldInit) == 0) return false;
            if (type.IsDefined(typeof(CompilerGeneratedAttribute), false)) return false;   // the compiler's: no script code
            var initializer = type.TypeInitializer;
            if (initializer == null) return false;
            try
            {
                var il = initializer.GetMethodBody()?.GetILAsByteArray();
                if (il == null) return false;
                var module = type.Module;
                var typeArguments = type.IsGenericType ? type.GetGenericArguments() : null;
                var i = 0;
                while (i < il.Length)
                {
                    var opCode = il[i] == 0xFE ? TwoByteOpCodes[il[++i]] : OneByteOpCodes[il[i]];
                    i++;
                    if (opCode.OperandType == OperandType.InlineMethod)
                    {
                        var method = module.ResolveMethod(BitConverter.ToInt32(il, i), typeArguments, null);
                        if (method.Module.Assembly == type.Assembly) return true;
                    }
                    else if (opCode.OperandType == OperandType.InlineField)
                    {
                        var owner = module.ResolveField(BitConverter.ToInt32(il, i), typeArguments, null).DeclaringType;
                        if (owner != null && owner != type && owner.Assembly == type.Assembly && owner.TypeInitializer != null) return true;
                    }
                    i += OperandSize(opCode.OperandType, il, i);
                }
                return false;
            }
            catch (Exception)
            {
                return true;   // not read: taken as the script's code
            }
        }

        private static readonly OpCode[] OneByteOpCodes = new OpCode[0x100];
        private static readonly OpCode[] TwoByteOpCodes = OpCodeTables(OneByteOpCodes);

        private static OpCode[] OpCodeTables(OpCode[] oneByte)
        {
            var twoByte = new OpCode[0x100];
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var opCode = (OpCode)field.GetValue(null);
                var value = (ushort)opCode.Value;
                if (opCode.Size == 1) oneByte[value] = opCode;
                else twoByte[value & 0xFF] = opCode;
            }
            return twoByte;
        }

        private static int OperandSize(OperandType operandType, byte[] il, int at)
        {
            switch (operandType)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return 4 + 4 * BitConverter.ToInt32(il, at);
                default: return 4;
            }
        }
    }
}
