using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using NLog;
using NLog.Fluent;
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
using VRage.Game.ModAPI;
using VRage.Groups;
using VRage.Utils;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class PBFix
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static ConcurrentDictionary<long, byte> needUpdateGridBlocksOwnership =
            new ConcurrentDictionary<long, byte>();
        
        public static ConcurrentDictionary<MyProgrammableBlock, int> pbOverHeatDict =
            new ConcurrentDictionary<MyProgrammableBlock, int>();
        public static Dictionary<long, int> Cooldowns = new Dictionary<long, int>();
        public static readonly Random r = new Random();
        
        public static void Patch(PatchContext ctx)
        {
            var RunSandboxedProgramAction = typeof(MyProgrammableBlock).GetMethod
                (nameof(MyProgrammableBlock.RunSandboxedProgramAction), BindingFlags.Instance | BindingFlags.Public);

            ctx.GetPattern(RunSandboxedProgramAction).Prefixes.Add(
                typeof(PBFix).GetMethod(nameof(RunSandboxedProgramActionPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var assembly = typeof(MyProgrammableBlock).Assembly;
            var typeMyCubeGridOwnershipManager =
                assembly.GetType("Sandbox.Game.Entities.Cube.MyCubeGridOwnershipManager");

            var RecalculateOwnersInternal = typeMyCubeGridOwnershipManager.GetMethod
                ("RecalculateOwnersInternal", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(RecalculateOwnersInternal).Prefixes.Add(
                typeof(PBFix).GetMethod(nameof(RecalculateOwnersInternalPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            
            var CompileAction = typeof(MyProgrammableBlock).GetMethod
                ("Compile", BindingFlags.Instance | BindingFlags.NonPublic);
            
            ctx.GetPattern(CompileAction).Prefixes.Add(
                typeof(PBFix).GetMethod(nameof(CompileActionPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        
        private static bool CompileActionPatched(MyProgrammableBlock __instance, string program)
        {
            if (program != null && program.Contains("double maxCurrentMs = 0.5;"))
            {
                var newProgram = program.Replace("double maxCurrentMs = 0.5;", "double maxCurrentMs = 0.1;");
                __instance.UpdateProgram(newProgram);
                return false;
            }
            return true;
        }
        
        private static void RecalculateOwnersInternalPatched(Object __instance)
        {
            MyCubeGrid cubeGrid =
                (MyCubeGrid) ReflectionUtils.GetInstanceField(__instance.GetType(), __instance, "m_grid");
            needUpdateGridBlocksOwnership[cubeGrid.EntityId] = 0;
        }

        private static bool RunSandboxedProgramActionPatched(MyProgrammableBlock __instance,
            ref MyProgrammableBlock.ScriptTerminationReason __result, Action<IMyGridProgram> action,
            ref string response)
        {
            try
            {
                var blockId = __instance.EntityId;
                var gridId = __instance.CubeGrid.EntityId;

                if (SentisOptimisationsPlugin.Config.SlowdownEnabled && MySandboxGame.Static.SimulationFrameCounter > 6000)
                {
                    var myUpdateTiersPlayerPresence = __instance.CubeGrid.PlayerPresenceTier;
                    // if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier1)
                    // {
                    //     if (NeedSkip(blockId, 30)) return false;
                    // } else
                    if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier2)
                    {
                        if (NeedSkip(blockId, 300)) return false;
                    }  
                }

                if (MySandboxGame.Static.UpdateThread != Thread.CurrentThread &&
                    MyVRage.Platform.Scripting.ReportIncorrectBehaviour(MyCommonTexts
                        .ModRuleViolation_PBParallelInvocation))
                    MyLog.Default.Log(MyLogSeverity.Error,
                        "PB invoked from parallel thread (logged only once)!" + Environment.NewLine +
                        Environment.StackTrace);
                bool m_isRunning =
                    (bool) ReflectionUtils.GetInstanceField(typeof(MyProgrammableBlock), __instance, "m_isRunning");
                if (m_isRunning)
                {
                    response = MyTexts.GetString(MySpaceTexts.ProgrammableBlock_Exception_AllreadyRunning);
                    __result = MyProgrammableBlock.ScriptTerminationReason.AlreadyRunning;
                    return false;
                }

                MyProgrammableBlock.ScriptTerminationReason m_terminationReason =
                    (MyProgrammableBlock.ScriptTerminationReason)
                    ReflectionUtils.GetInstanceField(typeof(MyProgrammableBlock), __instance, "m_terminationReason");
                if (m_terminationReason != MyProgrammableBlock.ScriptTerminationReason.None)
                {
                    response = __instance.DetailedInfo.ToString();
                    __result = m_terminationReason;
                    return false;
                }

                __instance.DetailedInfo.Clear();
                StringBuilder m_echoOutput =
                    (StringBuilder) ReflectionUtils.GetInstanceField(typeof(MyProgrammableBlock), __instance,
                        "m_echoOutput");
                m_echoOutput.Clear();
                Assembly m_assembly =
                    (Assembly) ReflectionUtils.GetInstanceField(typeof(MyProgrammableBlock), __instance, "m_assembly");
                if (m_assembly == (Assembly) null)
                {
                    response = MyTexts.GetString(MySpaceTexts.ProgrammableBlock_Exception_NoAssembly);
                    __result = MyProgrammableBlock.ScriptTerminationReason.NoScript;
                    return false;
                }

                IMyGridProgram m_instance =
                    (IMyGridProgram) ReflectionUtils.GetInstanceField(typeof(MyProgrammableBlock), __instance,
                        "m_instance");
                if (m_instance == null)
                {
                    bool m_needsInstantiation = (bool) ReflectionUtils.GetInstanceField(typeof(MyProgrammableBlock),
                        __instance, "m_needsInstantiation");
                    bool IsWorking = (bool) ReflectionUtils.InvokeInstanceMethod(typeof(MyProgrammableBlock),
                        __instance, "CheckIsWorking", new Object[0]);
                    if (m_needsInstantiation && IsWorking && __instance.Enabled)
                    {
                        ReflectionUtils.SetInstanceField(typeof(MyProgrammableBlock), __instance,
                            "m_needsInstantiation", false);
                        IEnumerable<string> m_compilerErrors =
                            (IEnumerable<string>) ReflectionUtils.GetInstanceField(typeof(MyProgrammableBlock),
                                __instance, "m_compilerErrors");
                        string m_storageData = (string) ReflectionUtils.GetInstanceField(typeof(MyProgrammableBlock),
                            __instance, "m_storageData");
                        ReflectionUtils.InvokeInstanceMethod(typeof(MyProgrammableBlock), __instance, "CreateInstance",
                            new Object[] {m_assembly, m_compilerErrors, m_storageData});
                        if (m_instance == null)
                        {
                            response = __instance.DetailedInfo.ToString();
                            __result = m_terminationReason;
                            return false;
                        }
                    }
                    else
                    {
                        response = MyTexts.GetString(MySpaceTexts.ProgrammableBlock_Exception_NoAssembly);
                        __result = MyProgrammableBlock.ScriptTerminationReason.NoScript;
                        return false;
                    }
                }

                MyGroups<MyCubeGrid, MyGridLogicalGroupData>.Group group =
                    MyCubeGridGroups.Static.Logical.GetGroup(__instance.CubeGrid);
                MyGridTerminalSystem terminalSystem =
                    (MyGridTerminalSystem) ReflectionUtils.GetInstanceField(typeof(MyGridLogicalGroupData),
                        group.GroupData, "TerminalSystem");
                //MyGridTerminalSystem terminalSystem = group.GroupData.TerminalSystem;
                MyProgrammableBlock.MyGridTerminalWrapper m_terminalWrapper =
                    (MyProgrammableBlock.MyGridTerminalWrapper) ReflectionUtils.GetInstanceField(
                        typeof(MyProgrammableBlock), __instance, "m_terminalWrapper");
                ReflectionUtils.InvokeInstanceMethod(typeof(MyProgrammableBlock.MyGridTerminalWrapper),
                    m_terminalWrapper, "SetInstance", new Object[] {terminalSystem});
                //m_terminalWrapper.SetInstance(terminalSystem);
                List<MyCubeGrid> m_groupCache =
                    (List<MyCubeGrid>) ReflectionUtils.GetInstanceField(typeof(MyProgrammableBlock), __instance,
                        "m_groupCache");
                MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Logical)
                    .GetGroupNodes(__instance.CubeGrid, m_groupCache);

                //group.GroupData.UpdateGridOwnership(m_groupCache, __instance.OwnerId);

                ReflectionUtils.InvokeInstanceMethod(group.GroupData.GetType(), group.GroupData, "UpdateGridOwnership",
                    new Object[] {m_groupCache, __instance.OwnerId});

                m_groupCache.Clear();
                if (terminalSystem != null)
                {
                    if (needUpdateGridBlocksOwnership.ContainsKey(gridId))
                    {
                        terminalSystem.UpdateGridBlocksOwnership(__instance.OwnerId);
                        needUpdateGridBlocksOwnership.Remove(gridId);
                    }
                }
                else
                    MyLog.Default.Critical("Probrammable block terminal system is null! Crash");

                m_instance.GridTerminalSystem = m_terminalWrapper;
                var objects = new Object[] {action, null};
                Stopwatch sw = new Stopwatch();
                sw.Start();
                __result = (MyProgrammableBlock.ScriptTerminationReason) ReflectionUtils.InvokeInstanceMethod(
                    typeof(MyProgrammableBlock),
                    __instance, "RunSandboxedProgramActionCore", objects);
                var scriptExecTime = sw.ElapsedMilliseconds;
                if (scriptExecTime > SentisOptimisationsPlugin.Config.ScriptsMaxExecTime && MySandboxGame.Static.SimulationFrameCounter > 10800)
                {
                    try
                    {
                        var ownerId = PlayerUtils.GetOwner(__instance.CubeGrid);
                        var playerIdentity = PlayerUtils.GetPlayerIdentity(ownerId);
                        var playerName = playerIdentity == null ? "---" : playerIdentity.DisplayName;
                        Log.Warn(
                            $"Script execution time exceeded {scriptExecTime} ms. \n PB - ({__instance.CustomName}) on - ({__instance.CubeGrid.DisplayName}) Owner - ({playerName})");
                        if (SentisOptimisationsPlugin.Config.EnableScriptsPunish)
                        {
                            var count = 0;
                            if (pbOverHeatDict.TryGetValue(__instance, out count))
                            {
                                count = count + 1;
                            }

                            if (count > SentisOptimisationsPlugin.Config.ScriptOvertimeExecTimesBeforePunish)
                            {
                                __instance.Enabled = false;
                                var sb = __instance.SlimBlock;
                                sb.DecreaseMountLevelToDesiredRatio(
                                    __instance.BlockDefinition.CriticalIntegrityRatio - 0.1f, null);
                                pbOverHeatDict.Remove(__instance);
                                Log.Error(
                                    $"Punish PB {scriptExecTime} ms. \n PB - ({__instance.CustomName}) on - ({__instance.CubeGrid.DisplayName}) Owner - ({playerName})");
                                ChatUtils.SendTo(ownerId,
                                    $"Script execution time exceeded PB - ({__instance.CustomName}) on - ({__instance.CubeGrid.DisplayName}) block disabled");
                                MyVisualScriptLogicProvider.ShowNotification(
                                    $"Script execution time exceeded PB - ({__instance.CustomName}) on - ({__instance.CubeGrid.DisplayName}) block disabled",
                                    5000,
                                    "Red", ownerId);
                            }
                            else
                            {
                                ChatUtils.SendTo(ownerId,
                                    $"Script execution time exceeded PB - ({__instance.CustomName}) on - ({__instance.CubeGrid.DisplayName}) will be disabled after {SentisOptimisationsPlugin.Config.ScriptOvertimeExecTimesBeforePunish + 1 - count} times");
                                pbOverHeatDict[__instance] = count;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "Punish pb exc");
                    }
                }
                response = (string) objects[1];
                //__result = __instance.RunSandboxedProgramActionCore(action, out response);
                return false;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn("RunSandboxedProgramActionPatched Exception " + __instance.DisplayName + " grid " + __instance.CubeGrid.DisplayName, e);
                __instance.Enabled = false;
            }

            return false;
        }

        private static bool NeedSkip(long blockId, int cd)
        {
            int cooldown;
            if (Cooldowns.TryGetValue(blockId, out cooldown))
            {
                if (cooldown > cd)
                {
                    Cooldowns[blockId] = 0;
                    return false;
                }
                Cooldowns[blockId] = cooldown + 1;
                return true;
            }

            Cooldowns[blockId] = r.Next(0, cd);
            return true;
        }
    }
}