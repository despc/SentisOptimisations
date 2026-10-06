using System;
using Sandbox.Game.World;
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

        /// <summary>Scripts are punished only well after the world loaded, so a loading world cannot condemn a script.</summary>
        private const ulong FramesBeforePunish = 10800;

        /// <summary>How often the owner of a script over its budget is told about it.</summary>
        private static readonly TimeSpan WarnEvery = TimeSpan.FromMinutes(1);

        /// <summary>Grids whose ownership changed and whose terminal system needs a refresh.</summary>
        public static ConcurrentDictionary<long, byte> needUpdateGridBlocksOwnership =
            new ConcurrentDictionary<long, byte>();


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
        private static readonly Action<MyProgrammableBlock, IMyGridProgram> SetInstance = Setter<IMyGridProgram>("m_instance");
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
            SubscribeFrameEnd();
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

            try
            {
                PbCompile.Bind();
                _offThreadCompile = true;
            }
            catch (Exception e)
            {
                Log.Error(e, "PBFix: scripts are compiled on the game thread, as vanilla does");
            }
            ctx.GetPattern(typeof(MyProgrammableBlock).GetMethod("Compile", BindingFlags.Instance | BindingFlags.NonPublic))
                .Prefixes.Add(self.GetMethod(nameof(CompileActionPatched), statics));
        }

        /// <summary>Drops what is remembered about a programmable block that is gone.</summary>
        public static void CleanupEntity(MyEntity entity)
        {
            if (!(entity is MyProgrammableBlock pb)) return;
            PbLoad.Forget(pb);
        }

        /// <summary>
        /// A widespread script measures itself against a budget of half a millisecond per run; on a
        /// server that is already too much, so the number is rewritten to a tenth as the script is
        /// compiled.
        /// </summary>
        private static bool CompileActionPatched(MyProgrammableBlock __instance, string program, string storage, bool instantiate)
        {
            try
            {
                if (program != null && program.Contains("double maxCurrentMs = 0.5;"))
                {
                    UpdateProgramStringMethod.Invoke(__instance,
                        new object[] { program.Replace("double maxCurrentMs = 0.5;", "double maxCurrentMs = 0.1;") });
                    return false;
                }
                // the compile on a worker and the script compiled to machine code there (PbCompile); vanilla where it cannot
                return !(_offThreadCompile && PbCompile.Start(__instance, program, storage, instantiate));
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
        /// Whether saving the block runs code of its script: the block's builder calls the script's Save() when there
        /// is a script and it has one (MyProgrammableBlock.UpdateStorage), and a Save() with nothing in it does nothing.
        /// </summary>
        public static bool SaveHasCode(MyProgrammableBlock pb)
        {
            var program = Instance(pb);
            return program != null && program.HasSaveMethod && Optimizer.Optimizations.ScriptSaveCode.HasCode(program.GetType());
        }

        /// <summary>
        /// Replaces <c>MyProgrammableBlock.RunSandboxedProgramAction</c>: the same sequence as vanilla,
        /// with the ownership refresh made conditional and the time of the run measured.
        /// </summary>
        private static bool RunSandboxedProgramActionPatched(MyProgrammableBlock __instance,
            ref MyProgrammableBlock.ScriptTerminationReason __result, Action<IMyGridProgram> action,
            ref string response)
        {
            try
            {
                // A run off the game thread (the script's Save() as a frozen grid's builder is made on a worker in a
                // parallel world save) is noted once, with the diagnostic logs on (it is how the save is made, not a fault). Vanilla reports it to MyModWatchdog.ReportIncorrectBehaviour, which
                // outside a mod's context reads no mod (ModInfo[0]) and throws: that threw here, and the block was switched
                // off for it - players' blocks went off at every save (production, 04.10.2026).
                if (global::SentisOptimisations.DiagLog.On && !_parallelRunNoted && MySandboxGame.Static.UpdateThread != Thread.CurrentThread)
                {
                    _parallelRunNoted = true;
                    Log.Warn("PB " + __instance.CustomName + " on " + __instance.CubeGrid?.DisplayName +
                             " run off the game thread (noted once):" + Environment.NewLine + Environment.StackTrace);
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

                // The grid not yet in its logical group (it is being added to the world - pasted, spawned, streamed in -
                // and something asked the block to run before its first frame): nothing to run against yet. The run is
                // put off, the instantiation still to come with the block's first update, as vanilla has it. (Taken on,
                // the instance was made, its constructor's run threw on the missing group, and the block was switched
                // off with no constructor run and no update frequency: the script never ran again, even switched on.)
                if (MyCubeGridGroups.Static.Logical.GetGroup(__instance.CubeGrid) == null)
                {
                    // an instance made already (its constructor's run is this one): dropped, to be made again with the
                    // block's next update, once the grid has its group - else it stood with no constructor run, never to run
                    if (Instance(__instance) != null)
                    {
                        SetInstance(__instance, null);
                        SetNeedsInstantiation(__instance, true);
                        __instance.NeedsUpdate |= VRage.ModAPI.MyEntityUpdateEnum.EACH_FRAME;
                    }
                    response = MyTexts.GetString(MySpaceTexts.ProgrammableBlock_Exception_NoAssembly);
                    __result = MyProgrammableBlock.ScriptTerminationReason.NoScript;
                    return false;
                }

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
                // An exception here is this prefix's own, not the script's (the script's are caught inside the run and
                // end it as vanilla does): the block is left on, the run just not made this time.
                if (WarnDue(__instance.EntityId))
                    Log.Warn(e, "RunSandboxedProgramActionPatched Exception " + __instance.DisplayName +
                                " grid " + __instance.CubeGrid?.DisplayName + "; run from:" + Environment.NewLine + Environment.StackTrace);
                response = e.Message;
                __result = MyProgrammableBlock.ScriptTerminationReason.None;
                return false;
            }
        }

        // ------------------------------------------------------------------ measurement

        /// <summary>
        /// Runs the script and charges the time to the block. A collection during the run makes the
        /// measurement meaningless, so the run is counted but its time is dropped.
        ///
        /// Only a run on the game thread is measured. A script also runs elsewhere - its Save() when the grid's
        /// builder is made, which a parallel world save did on its workers - and the punishment damages the block
        /// and remakes its Havok bodies: done off the game thread that corrupted native memory and crashed the
        /// server (28-29.09.2026).
        /// </summary>
        private static MyProgrammableBlock.ScriptTerminationReason Run(MyProgrammableBlock pb,
            Action<IMyGridProgram> action, out string response)
        {
            if (MySandboxGame.Static?.UpdateThread != System.Threading.Thread.CurrentThread)
                return RunCore(pb, action, out response);
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
                if (Optimizer.Optimizations.FrameClock.Active) Pending.Add(new PendingRun { Pb = pb, Ms = ms, Noisy = noisy });
                else Measure(pb, ms, noisy);
            }
        }

        private struct PendingRun
        {
            public MyProgrammableBlock Pb;
            public double Ms;
            public bool Noisy;
        }

        // this frame's runs, judged when the frame is over (game thread)
        private static readonly List<PendingRun> Pending = new List<PendingRun>();

        private static bool _parallelRunNoted;
        private static bool _offThreadCompile;
        private static readonly ConcurrentDictionary<long, DateTime> FailureWarned = new ConcurrentDictionary<long, DateTime>();

        /// <summary>True at most once a minute for a block: a failing run of a script on Update1 would flood the log.</summary>
        private static bool WarnDue(long entityId)
        {
            var now = DateTime.UtcNow;
            if (FailureWarned.TryGetValue(entityId, out var last) && now - last < TimeSpan.FromMinutes(1)) return false;
            FailureWarned[entityId] = now;
            return true;
        }
        private static bool _subscribed;

        /// <summary>Frames whose script runs were not counted (a collection, a save, a spike), for the GUI and the stand.</summary>
        public static long NoisyFramesSkipped;

        /// <summary>
        /// The runs of a frame count only when the frame was an ordinary one. In a frame with a garbage collection, during
        /// a world save, or in a spike (a save's snapshot, a big grid spawned) a script's time says more about the frame
        /// than about the script: a script over its budget only in such frames was punished for the server's own load.
        /// </summary>
        private static void OnFrameEnded(double frameMs, bool noisyFrame)
        {
            if (Pending.Count == 0) return;
            if (noisyFrame) NoisyFramesSkipped++;
            foreach (var run in Pending)
                Measure(run.Pb, run.Ms, run.Noisy || noisyFrame);
            Pending.Clear();
        }

        internal static void SubscribeFrameEnd()
        {
            if (_subscribed) return;
            _subscribed = true;
            Optimizer.Optimizations.FrameClock.FrameEnded += OnFrameEnded;
        }

        private static void Measure(MyProgrammableBlock pb, double ms, bool noisy)
        {
            try
            {
                var config = SentisOptimisationsPlugin.Config;
                var verdict = PbLoad.Record(pb, ms, noisy, config.ScriptsMaxExecTime, config.ScriptsMaxMsPerFrame,
                    config.ScriptOvertimeExecTimesBeforePunish, Instance(pb));
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

        /// <summary>Frames between the verdict and the punishment.</summary>
        private const int PunishDelayFrames = 30;

        // the blocks with a punishment on its way (from any thread)
        private static readonly ConcurrentDictionary<long, byte> PunishPending = new ConcurrentDictionary<long, byte>();

        /// <summary>
        /// The punishment is done <see cref="PunishDelayFrames"/> frames later on the game thread: it changes the world
        /// (the block damaged, its Havok bodies remade), which must never happen on another thread - a script also runs
        /// where it is measured, and done off the game thread it corrupted native memory (28-29.09.2026) - and not in the
        /// heavy frame in which the script was caught either.
        /// </summary>
        private static void Punish(MyProgrammableBlock pb, long ownerId, string what, double ms, double load)
        {
            if (!PunishPending.TryAdd(pb.EntityId, 0)) return;
            var at = (MySession.Static?.GameplayFrameCounter ?? 0) + PunishDelayFrames;
            MySandboxGame.Static?.Invoke(() =>
            {
                PunishPending.TryRemove(pb.EntityId, out _);
                if (pb.MarkedForClose || pb.Closed) return;
                PunishNow(pb, ownerId, what, ms, load);
            }, "PBFix.Punish", at);
        }

        private static void PunishNow(MyProgrammableBlock pb, long ownerId, string what, double ms, double load)
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

        // ------------------------------------------------------------------ binding helpers

        private static Func<MyProgrammableBlock, T> Getter<T>(string field)
        {
            var info = typeof(MyProgrammableBlock).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (info == null) throw new MissingFieldException("MyProgrammableBlock." + field);
            global::SentisOptimisationsPlugin.Accessors.CheckRead(info, typeof(T));
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
            global::SentisOptimisationsPlugin.Accessors.CheckWrite(info, typeof(T));
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
            if (field != null) global::SentisOptimisationsPlugin.Accessors.CheckRead(field, typeof(TField));
            else if (!global::SentisOptimisationsPlugin.Accessors.Compatible(property.PropertyType, typeof(TField)))
                throw new InvalidCastException(typeof(TOwner).Name + "." + name + " is " + property.PropertyType.FullName + ", not read as " + typeof(TField).FullName);
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
