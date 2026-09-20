using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using NLog;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Localization;
using Sandbox.ModAPI;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Groups;
using VRage.Utils;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// Programmable blocks: the ownership refresh they force, how often they run, and what they cost.
    ///
    /// <b>Grid ownership is refreshed only when it changed.</b> Vanilla calls
    /// <c>MyGridTerminalSystem.UpdateGridBlocksOwnership</c> on every run of every programmable block,
    /// which walks the blocks of the whole logical group. The ownership recalculation of a grid is
    /// patched to mark that grid instead, and the refresh runs on the next script run of a marked grid.
    ///
    /// <b>Scripts of grids nobody is near run rarely.</b> With <c>SlowdownEnabled</c>, a block on a
    /// grid in the second player-presence tier runs once in <see cref="Tier2Period"/> invocations,
    /// blocks spread over those invocations by a random start.
    ///
    /// <b>The load of each block is measured</b> - see <see cref="PbLoad"/> for what is measured and
    /// why - and a block that is over its budget in most of its recent runs is switched off and
    /// damaged below its critical integrity, as before.
    ///
    /// Everything the prefix needs from the private parts of <see cref="MyProgrammableBlock"/> is
    /// bound once into delegates: the previous version did a dozen <c>FieldInfo.GetValue</c> calls and
    /// a <c>MethodInfo.Invoke</c> on every single run of every script.
    /// </summary>
    [PatchShim]
    public static class PBFix
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>One run in this many is let through for a grid of the second presence tier.</summary>
        private const int Tier2Period = 300;

        /// <summary>Scripts are left alone until the world has settled after load.</summary>
        private const ulong FramesBeforeSlowdown = 6000;

        /// <summary>And punished only well after that, so a loading world cannot condemn a script.</summary>
        private const ulong FramesBeforePunish = 10800;

        /// <summary>How often the owner of a script over its budget is told about it.</summary>
        private static readonly TimeSpan WarnEvery = TimeSpan.FromMinutes(1);

        /// <summary>Grids whose ownership changed and whose terminal system needs a refresh.</summary>
        public static ConcurrentDictionary<long, byte> needUpdateGridBlocksOwnership =
            new ConcurrentDictionary<long, byte>();

        private static readonly ConcurrentDictionary<long, int> Cooldowns = new ConcurrentDictionary<long, int>();
        private static readonly Random Random = new Random();

        // ------------------------------------------------------------------ vanilla bindings

        private delegate MyProgrammableBlock.ScriptTerminationReason RunCoreDelegate(
            MyProgrammableBlock pb, Action<IMyGridProgram> action, out string response);

        private static readonly Func<MyProgrammableBlock, bool> IsRunning = Getter<bool>("m_isRunning");
        private static readonly Func<MyProgrammableBlock, MyProgrammableBlock.ScriptTerminationReason> TerminationReason =
            Getter<MyProgrammableBlock.ScriptTerminationReason>("m_terminationReason");
        private static readonly Func<MyProgrammableBlock, StringBuilder> EchoOutput = Getter<StringBuilder>("m_echoOutput");
        private static readonly Func<MyProgrammableBlock, Assembly> CurrentAssembly = Getter<Assembly>("m_currentAssembly");
        private static readonly Func<MyProgrammableBlock, IMyGridProgram> Instance = Getter<IMyGridProgram>("m_instance");
        private static readonly Func<MyProgrammableBlock, bool> NeedsInstantiation = Getter<bool>("m_needsInstantiation");
        private static readonly Action<MyProgrammableBlock, bool> SetNeedsInstantiation = Setter<bool>("m_needsInstantiation");
        private static readonly Func<MyProgrammableBlock, object> CompilerErrors = Getter<object>("m_compilerErrors");
        private static readonly Func<MyProgrammableBlock, string> StorageData = Getter<string>("m_storageData");
        private static readonly Func<MyProgrammableBlock, object> TerminalWrapper = Getter<object>("m_terminalWrapper");
        private static readonly Func<MyProgrammableBlock, List<MyCubeGrid>> GroupCache = Getter<List<MyCubeGrid>>("m_groupCache");

        private static readonly Func<MyProgrammableBlock, bool> CheckIsWorking = Method<Func<MyProgrammableBlock, bool>>("CheckIsWorking");
        private static readonly RunCoreDelegate RunCore = Method<RunCoreDelegate>("RunSandboxedProgramActionCore");
        private static readonly MethodInfo CreateInstanceMethod =
            typeof(MyProgrammableBlock).GetMethod("CreateInstance", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly MethodInfo UpdateProgramStringMethod = typeof(MyProgrammableBlock)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .First(m => m.Name == "UpdateProgram" && m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType == typeof(string));

        private static readonly Func<MyGridLogicalGroupData, MyGridTerminalSystem> TerminalSystemOf =
            GroupGetter<MyGridLogicalGroupData, MyGridTerminalSystem>("TerminalSystem");
        private static readonly Action<MyGridLogicalGroupData, List<MyCubeGrid>, long> UpdateGridOwnership =
            GroupMethod<Action<MyGridLogicalGroupData, List<MyCubeGrid>, long>>(typeof(MyGridLogicalGroupData), "UpdateGridOwnership");
        private static readonly Action<object, MyGridTerminalSystem> SetTerminalInstance = BuildSetTerminalInstance();

        // ------------------------------------------------------------------ patches

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("PBFix", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            var self = typeof(PBFix);

            // Bind everything now: if the game moved a member, this throws here and the patch is
            // skipped, which leaves vanilla behaviour instead of a prefix that fails on every run.
            if (RunCore == null || CheckIsWorking == null || SetTerminalInstance == null ||
                TerminalSystemOf == null || UpdateGridOwnership == null || CreateInstanceMethod == null)
            {
                throw new InvalidOperationException("PBFix: a member of MyProgrammableBlock could not be bound");
            }

            ctx.GetPattern(typeof(MyProgrammableBlock).GetMethod("RunSandboxedProgramAction", any))
                .Prefixes.Add(self.GetMethod(nameof(RunSandboxedProgramActionPatched), statics));

            var ownership = typeof(MyProgrammableBlock).Assembly
                .GetType("Sandbox.Game.Entities.Cube.MyCubeGridOwnershipManager")
                .GetMethod("RecalculateOwnersInternal", BindingFlags.Instance | BindingFlags.NonPublic);
            ctx.GetPattern(ownership).Prefixes.Add(self.GetMethod(nameof(RecalculateOwnersInternalPatched), statics));

            ctx.GetPattern(typeof(MyProgrammableBlock).GetMethod("Compile", BindingFlags.Instance | BindingFlags.NonPublic))
                .Prefixes.Add(self.GetMethod(nameof(CompileActionPatched), statics));
        }

        /// <summary>Drops what is remembered about a programmable block that is gone.</summary>
        public static void CleanupEntity(MyEntity entity)
        {
            if (!(entity is MyProgrammableBlock pb)) return;
            PbLoad.Forget(pb);
            Cooldowns.TryRemove(pb.EntityId, out _);
        }

        /// <summary>
        /// A widespread script measures itself against a budget of half a millisecond per run; on a
        /// server that is already too much, so the number is rewritten to a tenth as the script is
        /// compiled.
        /// </summary>
        private static bool CompileActionPatched(MyProgrammableBlock __instance, string program)
        {
            try
            {
                if (program == null || !program.Contains("double maxCurrentMs = 0.5;")) return true;
                UpdateProgramStringMethod.Invoke(__instance,
                    new object[] { program.Replace("double maxCurrentMs = 0.5;", "double maxCurrentMs = 0.1;") });
                return false;
            }
            catch (Exception e)
            {
                Log.Error(e, "CompileActionPatched exception");
                return true;
            }
        }

        /// <summary>Marks the grid so the next script run on it refreshes the terminal ownership.</summary>
        private static void RecalculateOwnersInternalPatched(object __instance)
        {
            try
            {
                var grid = (MyCubeGrid)ReflectionUtils.GetInstanceField(__instance.GetType(), __instance, "m_grid");
                needUpdateGridBlocksOwnership[grid.EntityId] = 0;
            }
            catch (Exception e)
            {
                Log.Error(e, "RecalculateOwnersInternalPatched exception");
            }
        }

        /// <summary>
        /// Replaces <c>MyProgrammableBlock.RunSandboxedProgramAction</c>: the same sequence as vanilla,
        /// with the ownership refresh made conditional, the run skipped for a sleeping grid and the
        /// time of the run measured.
        /// </summary>
        private static bool RunSandboxedProgramActionPatched(MyProgrammableBlock __instance,
            ref MyProgrammableBlock.ScriptTerminationReason __result, Action<IMyGridProgram> action,
            ref string response)
        {
            try
            {
                if (SentisOptimisationsPlugin.Config.SlowdownEnabled &&
                    MySandboxGame.Static.SimulationFrameCounter > FramesBeforeSlowdown &&
                    __instance.CubeGrid.PlayerPresenceTier == MyUpdateTiersPlayerPresence.Tier2 &&
                    NeedSkip(__instance.EntityId, Tier2Period))
                {
                    return false;
                }

                if (MySandboxGame.Static.UpdateThread != Thread.CurrentThread &&
                    MyVRage.Platform.Scripting.ReportIncorrectBehaviour(MyCommonTexts.ModRuleViolation_PBParallelInvocation))
                {
                    MyLog.Default.Log(MyLogSeverity.Error,
                        "PB invoked from parallel thread (logged only once)!" + Environment.NewLine + Environment.StackTrace);
                }

                if (IsRunning(__instance))
                {
                    response = MyTexts.GetString(MySpaceTexts.ProgrammableBlock_Exception_AllreadyRunning);
                    __result = MyProgrammableBlock.ScriptTerminationReason.AlreadyRunning;
                    return false;
                }

                var terminationReason = TerminationReason(__instance);
                if (terminationReason != MyProgrammableBlock.ScriptTerminationReason.None)
                {
                    response = __instance.DetailedInfo.ToString();
                    __result = terminationReason;
                    return false;
                }

                __instance.DetailedInfo.Clear();
                EchoOutput(__instance).Clear();

                var assembly = CurrentAssembly(__instance);
                if (assembly == null)
                {
                    response = MyTexts.GetString(MySpaceTexts.ProgrammableBlock_Exception_NoAssembly);
                    __result = MyProgrammableBlock.ScriptTerminationReason.NoScript;
                    return false;
                }

                var program = Instance(__instance);
                if (program == null)
                {
                    if (!NeedsInstantiation(__instance) || !CheckIsWorking(__instance) || !__instance.Enabled)
                    {
                        response = MyTexts.GetString(MySpaceTexts.ProgrammableBlock_Exception_NoAssembly);
                        __result = MyProgrammableBlock.ScriptTerminationReason.NoScript;
                        return false;
                    }

                    SetNeedsInstantiation(__instance, false);
                    CreateInstanceMethod.Invoke(__instance,
                        new[] { assembly, CompilerErrors(__instance), StorageData(__instance) });
                    // Read the field again: the previous version tested the copy it had taken before
                    // the instance was created, so the first run after every compile was thrown away.
                    program = Instance(__instance);
                    if (program == null)
                    {
                        response = __instance.DetailedInfo.ToString();
                        __result = TerminationReason(__instance);
                        return false;
                    }
                }

                var group = MyCubeGridGroups.Static.Logical.GetGroup(__instance.CubeGrid);
                var terminalSystem = TerminalSystemOf(group.GroupData);
                var wrapper = TerminalWrapper(__instance);
                SetTerminalInstance(wrapper, terminalSystem);

                var groupCache = GroupCache(__instance);
                MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Logical).GetGroupNodes(__instance.CubeGrid, groupCache);
                UpdateGridOwnership(group.GroupData, groupCache, __instance.OwnerId);
                groupCache.Clear();

                if (terminalSystem == null)
                {
                    MyLog.Default.Critical("Programmable block terminal system is null! Crash");
                }
                else if (needUpdateGridBlocksOwnership.TryRemove(__instance.CubeGrid.EntityId, out _))
                {
                    // Vanilla does this on every run; the ownership patch says when it is needed.
                    terminalSystem.UpdateGridBlocksOwnership(__instance.OwnerId);
                }

                // MyGridTerminalWrapper implements the Ingame interface only, which is exactly what
                // the program's property takes; casting it to Sandbox.ModAPI.IMyGridTerminalSystem,
                // as this prefix used to, throws.
                program.GridTerminalSystem = (Sandbox.ModAPI.Ingame.IMyGridTerminalSystem)wrapper;
                try
                {
                    __result = Run(__instance, action, out response);
                }
                finally
                {
                    var current = Instance(__instance);
                    if (current != null) current.GridTerminalSystem = null;
                }

                return false;
            }
            catch (Exception e)
            {
                Log.Warn(e, "RunSandboxedProgramActionPatched Exception " + __instance.DisplayName +
                            " grid " + __instance.CubeGrid?.DisplayName);
                __instance.Enabled = false;
                return false;
            }
        }

        // ------------------------------------------------------------------ measurement

        /// <summary>
        /// Runs the script and charges the time to the block. A collection during the run makes the
        /// measurement meaningless, so the run is counted but its time is dropped.
        /// </summary>
        private static MyProgrammableBlock.ScriptTerminationReason Run(MyProgrammableBlock pb,
            Action<IMyGridProgram> action, out string response)
        {
            var collections = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                return RunCore(pb, action, out response);
            }
            finally
            {
                var ms = (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
                var noisy = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2) != collections;
                Measure(pb, ms, noisy);
            }
        }

        private static void Measure(MyProgrammableBlock pb, double ms, bool noisy)
        {
            try
            {
                var config = SentisOptimisationsPlugin.Config;
                var verdict = PbLoad.Record(pb, ms, noisy, config.ScriptsMaxExecTime, config.ScriptsMaxMsPerFrame,
                    config.ScriptOvertimeExecTimesBeforePunish);
                if (verdict == PbVerdict.Ok || MySandboxGame.Static.SimulationFrameCounter <= FramesBeforePunish) return;

                if (!config.EnableScriptsPunish) return;

                var ownerId = PlayerUtils.GetOwner(pb.CubeGrid);
                var load = PbLoad.LoadMsPerFrame(pb);
                if (verdict == PbVerdict.Warned)
                {
                    // Nothing goes to the log here: a script over its budget is over it on every run,
                    // and what every script costs is in the plugin's GUI. The owner is told at most
                    // once a minute.
                    if (PbLoad.WarnDue(pb, WarnEvery))
                    {
                        ChatUtils.SendTo(ownerId,
                            $"Script over budget (run {ms:F2} ms, {load:F2} ms every frame) PB - ({pb.CustomName}) " +
                            $"on - ({pb.CubeGrid.DisplayName}), the block will be disabled if it keeps that up");
                    }

                    return;
                }

                var playerIdentity = PlayerUtils.GetPlayerIdentity(ownerId);
                var playerName = playerIdentity == null ? "---" : playerIdentity.DisplayName;
                Punish(pb, ownerId,
                    $"PB - ({pb.CustomName}) on - ({pb.CubeGrid.DisplayName}) Owner - ({playerName})", ms, load);
            }
            catch (Exception e)
            {
                Log.Error(e, "Measuring a programmable block failed");
            }
        }

        private static void Punish(MyProgrammableBlock pb, long ownerId, string what, double ms, double load)
        {
            // Read the counters before forgetting the block, or the message reports zeroes.
            var overruns = PbLoad.Overruns(pb);
            pb.Enabled = false;
            pb.SlimBlock.DecreaseMountLevelToDesiredRatio(pb.BlockDefinition.CriticalIntegrityRatio - 0.1f, null);
            PbLoad.Forget(pb);
            Log.Error($"PB deconstructed for load: run {ms:F2} ms, {load:F2} ms of every frame, " +
                     $"{overruns} of last {PbLoad.WindowRuns} runs over budget. {what}");
            var message = $"Script execution time exceeded PB - ({pb.CustomName}) on - ({pb.CubeGrid.DisplayName}) block disabled";
            ChatUtils.SendTo(ownerId, message);
            MyVisualScriptLogicProvider.ShowNotification(message, 5000, "Red", ownerId);
        }

        /// <summary>
        /// True while the block is inside its cooldown. The first cooldown of a block starts at a
        /// random point, so the blocks of a world do not all run on the same invocation.
        /// </summary>
        private static bool NeedSkip(long blockId, int period)
        {
            if (!Cooldowns.TryGetValue(blockId, out var cooldown))
            {
                Cooldowns[blockId] = Random.Next(0, period);
                return true;
            }

            if (cooldown > period)
            {
                Cooldowns[blockId] = 0;
                return false;
            }

            Cooldowns[blockId] = cooldown + 1;
            return true;
        }

        // ------------------------------------------------------------------ binding helpers

        private static Func<MyProgrammableBlock, T> Getter<T>(string field)
        {
            var info = typeof(MyProgrammableBlock).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (info == null) throw new MissingFieldException("MyProgrammableBlock." + field);
            var method = new DynamicMethod("Get" + field, typeof(T), new[] { typeof(MyProgrammableBlock) },
                typeof(MyProgrammableBlock), true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, info);
            if (typeof(T) == typeof(object) && info.FieldType.IsValueType) il.Emit(OpCodes.Box, info.FieldType);
            il.Emit(OpCodes.Ret);
            return (Func<MyProgrammableBlock, T>)method.CreateDelegate(typeof(Func<MyProgrammableBlock, T>));
        }

        private static Action<MyProgrammableBlock, T> Setter<T>(string field)
        {
            var info = typeof(MyProgrammableBlock).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (info == null) throw new MissingFieldException("MyProgrammableBlock." + field);
            var method = new DynamicMethod("Set" + field, null, new[] { typeof(MyProgrammableBlock), typeof(T) },
                typeof(MyProgrammableBlock), true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, info);
            il.Emit(OpCodes.Ret);
            return (Action<MyProgrammableBlock, T>)method.CreateDelegate(typeof(Action<MyProgrammableBlock, T>));
        }

        private static T Method<T>(string name) where T : class
        {
            var info = typeof(MyProgrammableBlock).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (info == null) throw new MissingMethodException("MyProgrammableBlock." + name);
            return Delegate.CreateDelegate(typeof(T), info) as T;
        }

        private static Func<TOwner, TField> GroupGetter<TOwner, TField>(string name)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var field = typeof(TOwner).GetField(name, any);
            var property = field == null ? typeof(TOwner).GetProperty(name, any) : null;
            if (field == null && property == null) throw new MissingFieldException(typeof(TOwner).Name + "." + name);
            var method = new DynamicMethod("Get" + name, typeof(TField), new[] { typeof(TOwner) }, typeof(TOwner), true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            if (field != null) il.Emit(OpCodes.Ldfld, field);
            else il.Emit(OpCodes.Callvirt, property.GetMethod);
            il.Emit(OpCodes.Ret);
            return (Func<TOwner, TField>)method.CreateDelegate(typeof(Func<TOwner, TField>));
        }

        private static T GroupMethod<T>(Type owner, string name) where T : class
        {
            var info = owner.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (info == null) throw new MissingMethodException(owner.Name + "." + name);
            return Delegate.CreateDelegate(typeof(T), info) as T;
        }

        /// <summary>
        /// <c>MyProgrammableBlock.MyGridTerminalWrapper</c> is a private nested type, so its
        /// SetInstance is reached through a method that takes the wrapper as an object.
        /// </summary>
        private static Action<object, MyGridTerminalSystem> BuildSetTerminalInstance()
        {
            var wrapper = typeof(MyProgrammableBlock).GetNestedType("MyGridTerminalWrapper",
                BindingFlags.Public | BindingFlags.NonPublic);
            var setInstance = wrapper?.GetMethod("SetInstance",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (setInstance == null) throw new MissingMethodException("MyGridTerminalWrapper.SetInstance");
            var method = new DynamicMethod("SetTerminalInstance", null,
                new[] { typeof(object), typeof(MyGridTerminalSystem) }, typeof(MyProgrammableBlock), true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, wrapper);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, setInstance);
            il.Emit(OpCodes.Ret);
            return (Action<object, MyGridTerminalSystem>)method.CreateDelegate(typeof(Action<object, MyGridTerminalSystem>));
        }
    }
}
