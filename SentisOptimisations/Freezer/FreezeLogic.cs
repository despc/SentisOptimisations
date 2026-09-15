using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Havok;
using NAPI;
using Sandbox;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using SentisOptimisations;
using SentisOptimisations.DelayedLogic;
using SentisOptimisations.Utils;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.Entity.EntityComponents.Interfaces;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace SentisOptimisationsPlugin.Freezer;

public class FreezeLogic
{
    private static int _wakeupTimeInSec = 10;

    // Written by the game thread (freeze/unfreeze actions, entity-removal observer) and read/
    // written by the background FreezerLoop; plain HashSet would race and can throw or tear.
    public static ConcurrentHashSet<long> FrozenGrids = new();
    public static ConcurrentHashSet<long> FrozenPhysicsGrids = new();
    public static ConcurrentHashSet<long> InFreezeQueue = new();
    private static readonly Dictionary<long, DateTime> WakeUpDatas = new(); //EntityId:NextWakeUpTime
    private static readonly object _wakeUpLock = new();

    // sanity cap for one compensation: at 60 fps this is ~8 hours of simulation
    internal const ulong MaxCompensationFrames = 60UL * 60 * 8;

    // Written by the FreezerLoop thread, averaged by the same/other loops - lock it.
    private static readonly List<float> CpuLoads = new();
    private static readonly object CpuLoadLock = new();

    public void CheckGridGroup(HashSet<MyCubeGrid> grids)
    {
        try
        {
            var freezerEnabled = SentisOptimisationsPlugin.Config.FreezerEnabled;
            var anyGrid = grids.FirstElement();
            var gridsPosition = anyGrid.PositionComp.GetPosition();
            var isWakeUpTime = IsWakeUpTime(grids);
            var isAnyGridStatic = grids.Any(grid => grid.IsStatic);
            var freezeDistance = isAnyGridStatic
                ? SentisOptimisationsPlugin.Config.FreezeDistanceStatic
                : SentisOptimisationsPlugin.Config.FreezeDistanceDynamic;
            if (!freezerEnabled || PlayerUtils.IsAnyPlayersInRadius(gridsPosition, freezeDistance)
                || isWakeUpTime)
            {
                UnfreezeGrids(grids, isWakeUpTime);
                return;
            }

            FreezeGrids(grids);
        }
        catch (InvalidOperationException e)
        {
            // ignore "Collection was modified"
        }
    }

    private bool IsWakeUpTime(HashSet<MyCubeGrid> grids)
    {
        var minEntityId = grids.MinBy(grid => grid.EntityId).EntityId;
        if (WakeUpDatas.TryGetValue(minEntityId, out var data))
        {
            if (DateTime.Now < data && data < DateTime.Now.AddSeconds(_wakeupTimeInSec))
            {
                if (GetAvgCpuLoad() > 70)
                {
                    WakeUpDatas[minEntityId] = DateTime.Now.AddSeconds(SentisOptimisationsPlugin.Config.MinWakeUpIntervalInSec);
                    return false;
                }
                // grids.ForEach(grid => Log("Wake up time, grid - " + grid.DisplayName));

                return true;
            }
        }

        return false;
    }

    private void UnfreezeGrids(HashSet<MyCubeGrid> grids, bool isWakeUpTime)
    {
        var minEntityId = grids.MinBy(grid => grid.EntityId).EntityId;
        InFreezeQueue.Remove(minEntityId);

        var groupWithFixedGrid = GroupContainsFixedGrid(grids);

        var realyNeedToUnFreezeGrids = grids.Where(grid =>
        {
            if (!FrozenGrids.Contains(grid.EntityId))
            {
                return false;
            }

            if (grid.IsPreview)
            {
                return false;
            }

            return true;
        }).ToList();
        foreach (var grid in realyNeedToUnFreezeGrids)
        {

            if (!isWakeUpTime)
            {
                lock (_wakeUpLock)
                {
                    WakeUpDatas.Remove(grid.EntityId);
                }
            }

            if (grid.Parent == null)
            {
                Log("Unfreeze grid " + grid.DisplayName);
                FrozenGrids.Remove(grid.EntityId);
                InFreezeQueue.Remove(grid.EntityId);

                CompensateFrozenFrames(grid);
                
            }
            
        }
        MyAPIGateway.Utilities.InvokeOnGameThread(() =>
        {
            foreach (var grid in realyNeedToUnFreezeGrids)
            {
                if (!grid.IsStatic)
                {
                    var gridPhysics = grid.Physics;

                    if (gridPhysics != null && SentisOptimisationsPlugin.Config.FreezePhysics &&
                        !groupWithFixedGrid)
                    {
                        DoUnfreezePhysics(grid);
                        FrozenPhysicsGrids.Remove(grid.EntityId);
                    }
                }

                RegisterRecursive(grid);
                grid.PlayerPresenceTier = MyUpdateTiersPlayerPresence.Normal;  
            }
        });
    }

    private static void CompensateFrozenFrames(MyCubeGrid grid)
    {
        var frame = MySandboxGame.Static.SimulationFrameCounter;
        foreach (var myCubeBlock in grid.GetFatBlocks())
        {
            if (!(myCubeBlock is MyFunctionalBlock))
            {
                continue;
            }

            var blockId = myCubeBlock.EntityId;
            var needToCompensate = NeedToCompensate((MyFunctionalBlock)myCubeBlock);
            if (needToCompensate)
            {
                MyTimerComponent timer =
                    (MyTimerComponent)myCubeBlock.easyGetField("m_timer", typeof(MyFunctionalBlock));
                if (timer != null)
                {
                    // Accumulate the frozen period; a previous pending period is NOT overwritten.
                    if (!CompensationTracker.OnUnfrozen(blockId, frame, MaxCompensationFrames))
                        continue;
                    if (!CompensationTracker.TryScheduleApply(blockId))
                        continue; // an already-scheduled apply will pick everything up

                    // Apply after the unfrozen blocks have run a couple of normal frames. The
                    // apply takes the ACCUMULATED total atomically; if the block got frozen
                    // again in the meantime the total survives for the next apply.
                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    {
                        try
                        {
                            if (CompensationTracker.IsFrozen(blockId))
                            {
                                CompensationTracker.ReleaseSchedule(blockId);
                                return;
                            }

                            if (myCubeBlock.Closed || myCubeBlock.MarkedForClose)
                            {
                                CompensationTracker.Forget(blockId);
                                return;
                            }

                            if (CompensationTracker.TryTakeCompensation(blockId, out var framesAfterFreeze))
                            {
                                // ADD to whatever vanilla accumulated during the apply window
                                // instead of overwriting: overwriting silently dropped the
                                // ~2 s of real production of that window.
                                var vanilla = timer.FramesFromLastTrigger;
                                timer.FramesFromLastTrigger =
                                    uint.MaxValue - vanilla < framesAfterFreeze
                                        ? uint.MaxValue
                                        : vanilla + framesAfterFreeze;
                                grid.PlayerPresenceTier = MyUpdateTiersPlayerPresence.Normal;
                            }
                        }
                        catch (Exception ex)
                        {
                            CompensationTracker.ReleaseSchedule(blockId);
                            SentisOptimisationsPlugin.Log.Error(ex, "Compensate exception");
                        }
                    }, StartAt: (int)(frame + 120));
                }
            }
            else
            {
                CompensationTracker.Forget(blockId);
            }
        }
    }

    private void FreezeGrids(HashSet<MyCubeGrid> grids)
    {
        var configAntifreezeBlocksSubtypes = SentisOptimisationsPlugin.Config.AntifreezeBlocksSubtypes;
        var antifreezeBlocksSubtypes = configAntifreezeBlocksSubtypes.Split(':');
        foreach (var grid in grids)
        {
            if (!string.IsNullOrEmpty(configAntifreezeBlocksSubtypes) && grid.GetBlocks().Any(block =>
                    Enumerable.Contains(antifreezeBlocksSubtypes, block.BlockDefinition.Id.SubtypeName)))
            {
                // Log("Found antifreeze block, skip grid " + grid.DisplayName);
                return;
            }

            if (!SentisOptimisationsPlugin.Config.FreezeSignals && (grid.DisplayName.Contains("Container MK-") ||
                                                                    grid.DisplayName.Contains("Container_MK-")))
            {
                return;
            }

            if (!SentisOptimisationsPlugin.Config.FreezeNpc && grid.isNpcGrid())
            {
                // Log("Dont freeze NPC, skip grid " + grid.DisplayName);
                return;
            }
        }

        bool needToAwake = false;
        foreach (var myCubeGrid in grids)
        {
            if (myCubeGrid.GetFatBlocks()
                .Any(block => block is MyFunctionalBlock && NeedToCompensate((MyFunctionalBlock)block)))
            {
                needToAwake = true;
                break;
            }
        }

        var minEntityId = grids.MinBy(grid => grid.EntityId).EntityId;
        if (needToAwake)
        {
            lock (_wakeUpLock)
            {
                if (WakeUpDatas.TryGetValue(minEntityId, out var dateTime))
                {
                    if (DateTime.Now.AddSeconds(_wakeupTimeInSec) > dateTime)
                    {
                        WakeUpDatas[minEntityId] = DateTime.Now +
                                                   TimeSpan.FromSeconds(
                                                       SentisOptimisationsPlugin.Config.MinWakeUpIntervalInSec +
                                                       minEntityId % SentisOptimisationsPlugin.Config
                                                           .MinWakeUpIntervalInSec);
                    }
                    else if (DateTime.Now < dateTime && dateTime < DateTime.Now.AddSeconds(_wakeupTimeInSec))
                    {
                        return;
                    }
                }
                else
                {
                    WakeUpDatas.Add(minEntityId,
                        DateTime.Now + TimeSpan.FromSeconds(SentisOptimisationsPlugin.Config.MinWakeUpIntervalInSec +
                                                            minEntityId % SentisOptimisationsPlugin.Config
                                                                .MinWakeUpIntervalInSec));
                }
            }
        }

        // Add returns false when the id is already queued (ConcurrentDictionary.TryAdd)
        if (!InFreezeQueue.Add(minEntityId))
        {
            return;
        }

        var delayBeforeFreezeSec = SentisOptimisationsPlugin.Config
            .DelayBeforeFreezeSec;
        var groupWithFixedGrid = GroupContainsFixedGrid(grids);
        var needToFreezeGrids = grids.Where(grid => !FrozenGrids.Contains(grid.EntityId)).ToList();

        if (needToFreezeGrids.Count == 0)
        {
            InFreezeQueue.Remove(minEntityId);
            return;
        }

        Thread.Sleep(8);
        DelayedProcessor.Instance.AddDelayedAction(DateTime.Now.AddSeconds(delayBeforeFreezeSec), () =>
        {
            try
            {
                var realyNeedToFreezeGrids = grids.Where(grid =>
                {
                    if (grid == null || grid.Closed || grid.MarkedForClose || grid.IsPreview) return false;
                    if (FrozenGrids.Contains(grid.EntityId))
                    {
                        return false;
                    }

                    var isInQueue = InFreezeQueue.Contains(minEntityId);

                    return grid.Parent == null && isInQueue;
                }).ToList();

                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    foreach (var grid in realyNeedToFreezeGrids)
                    {
                        if (grid == null || grid.Closed || grid.MarkedForClose || grid.IsPreview) continue;

                        Log("Freeze grid " + grid.DisplayName);

                        if (!grid.IsStatic)
                        {
                            var gridPhysics = grid.Physics;
                            if (gridPhysics != null)
                            {
                                gridPhysics.SetSpeeds(Vector3.Zero, Vector3.Zero);
                                
                                if (SentisOptimisationsPlugin.Config.FreezePhysics && !groupWithFixedGrid)
                                {
                                    DoFreezePhysics(grid);
                                }
                                grid.RaisePhysicsChanged();
                            }
                        }

                        FrozenGrids.Add(grid.EntityId);
                        UnregisterRecursive(grid);

                        // Stamp the compensation clock on the game thread, at the exact frame the
                        // grid stops updating. Blocks that leave the world must drop their stamp
                        // (see ForgetBlocks below + EntitiesObserver): EntityIds are reused.
                        var frame = MySandboxGame.Static.SimulationFrameCounter;
                        foreach (var myCubeBlock in grid.GetFatBlocks())
                        {
                            if (myCubeBlock is MyFunctionalBlock functional && NeedToCompensate(functional))
                            {
                                CompensationTracker.OnFrozen(myCubeBlock.EntityId, frame);
                            }
                        }
                    }
                });
            }
            catch (Exception e)
            {
                //
            }


            InFreezeQueue.Remove(minEntityId);
        });
    }

    /// <summary>
    /// Drop every trace of grids that left the world. A stale freeze stamp under a recycled
    /// EntityId would hand the next holder of that id an absurd compensation delta.
    /// </summary>
    public static void ForgetGrid(MyCubeGrid grid)
    {
        FrozenGrids.Remove(grid.EntityId);
        FrozenPhysicsGrids.Remove(grid.EntityId);
        InFreezeQueue.Remove(grid.EntityId);
        lock (_wakeUpLock)
        {
            WakeUpDatas.Remove(grid.EntityId);
        }

        try
        {
            foreach (var block in grid.GetFatBlocks())
                CompensationTracker.Forget(block.EntityId);
        }
        catch (Exception e)
        {
            // grid already torn down; stamps for its blocks are dropped below on sweep
        }
    }

    private static bool GroupContainsFixedGrid(HashSet<MyCubeGrid> grids)
    {
        try
        {
            return grids.Any(grid =>
            {
                if (grid.IsStatic)
                {
                    return true;
                }

                if (grid.Physics == null)
                {
                    return false;
                }

                return new HashSet<HkConstraint>(grid.Physics.Constraints).Any(constraint =>
                {
                    if (constraint.RigidBodyB == null)
                    {
                        return false;
                    }

                    return constraint.RigidBodyB.UserObject is MyVoxelPhysicsBody;
                });
            });
        }
        catch (Exception e)
        {
            return true;
        }
    }

    private static void DoFreezePhysics(MyCubeGrid grid)
    {
        try
        {
            var gridPhysics = grid.Physics;
            gridPhysics.ConvertToStatic();
            FrozenPhysicsGrids.Add(grid.EntityId);
        }
        catch (Exception e)
        {
            //
        }
    }

    private static void DoUnfreezePhysics(MyCubeGrid grid)
    {
        var gridPhysics = grid.Physics;
        if (!FrozenPhysicsGrids.Contains(grid.EntityId))
        {
            return;
        }

        gridPhysics.ConvertToDynamic(grid.GridSizeEnum == MyCubeSize.Large, grid.IsClientPredicted);
        gridPhysics.SetSpeeds(Vector3.Zero, Vector3.Zero);
        grid.RecalculateGravity();
        grid.RaisePhysicsChanged();
    }

    public static bool NeedToCompensate(MyFunctionalBlock myCubeBlock)
    {
        if (myCubeBlock == null || !myCubeBlock.IsWorking)
        {
            return false;
        }

        var subtypeName = myCubeBlock.BlockDefinition.Id.SubtypeName;
        if (!string.IsNullOrEmpty(subtypeName))
        {
            if (subtypeName.Contains("Crusher"))
            {
                return false;
            }
        }
        var needToCompensate = myCubeBlock is MyProductionBlock;
        return needToCompensate;
    }

    private void UnregisterRecursive(MyEntity e)
    {
        MyEntities.UnregisterForUpdate(e);
        (e.GameLogic as IMyGameLogicComponent)?.UnregisterForUpdate();
        e.Flags |= (EntityFlags)4;
        if (e.Hierarchy == null) return;

        foreach (var child in e.Hierarchy.Children) UnregisterRecursive((MyEntity)child.Container.Entity);
    }

    private void RegisterRecursive(MyEntity e)
    {
        MyEntities.RegisterForUpdate(e);
        (e.GameLogic as IMyGameLogicComponent)?.RegisterForUpdate();
        e.Flags &= ~(EntityFlags)4;
        if (e.Hierarchy == null) return;

        foreach (var child in e.Hierarchy.Children) RegisterRecursive((MyEntity)child.Container.Entity);
    }


    public static void Log(string message)
    {
        if (SentisOptimisationsPlugin.Config.EnableDebugLogs)
        {
            SentisOptimisationsPlugin.Log.Warn(message);
        }
    }

    public static void CompensationLogs(string message)
    {
        if (SentisOptimisationsPlugin.Config.EnableCompensationLogs)
        {
            SentisOptimisationsPlugin.Log.Warn(message);
        }
    }

    public void UpdateCpuLoad(float cpuLoad)
    {
        lock (CpuLoadLock)
        {
            while (CpuLoads.Count > 30)
            {
                CpuLoads.RemoveAt(0);
            }

            CpuLoads.Add(cpuLoad);
        }
    }

    public static void UpdateFreezePhysics(bool freezePhysicsEnabled)
    {
        DelayedProcessor.Instance.AddDelayedAction(DateTime.Now, () =>
        {
            MyAPIGateway.Utilities.InvokeOnGameThread(() =>
            {
                var gridsList = new HashSet<long>(FrozenGrids);
                int i = 0;


                List<HashSet<IMyCubeGrid>> groups = new List<HashSet<IMyCubeGrid>>();
                while (gridsList.Count > 0)
                {
                    var gridEntityId = gridsList.FirstElement();
                    MyCubeGrid grid = (MyCubeGrid)MyEntities.GetEntityById(gridEntityId);
                    HashSet<IMyCubeGrid> group = new HashSet<IMyCubeGrid>();
                    MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Physical, group);
                    group.ForEach(cubeGrid => gridsList.Remove(cubeGrid.EntityId));
                    groups.Add(group);
                }

                foreach (var group in groups)
                {
                    i += 2;
                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    {
                        var groupWithFixedGrid =
                            GroupContainsFixedGrid(group.Select(cubeGrid => (MyCubeGrid)cubeGrid).ToHashSet());
                        foreach (var myCubeGrid in group)
                        {
                            var gridPhysics = myCubeGrid.Physics;
                            if (gridPhysics == null)
                            {
                                continue;
                            }

                            if (!myCubeGrid.IsStatic)
                            {
                                if (freezePhysicsEnabled)
                                {
                                    gridPhysics.SetSpeeds(Vector3.Zero, Vector3.Zero);
                                    if (!groupWithFixedGrid)
                                    {
                                        DoFreezePhysics((MyCubeGrid)myCubeGrid);
                                    }
                                }
                                else
                                {
                                    if (!groupWithFixedGrid)
                                    {
                                        gridPhysics.SetSpeeds(Vector3.Zero, Vector3.Zero);
                                        DoUnfreezePhysics((MyCubeGrid)myCubeGrid);
                                    }

                                    FrozenPhysicsGrids.Remove(myCubeGrid.EntityId);
                                }
                            }
                        }
                    }, StartAt: (int)MySandboxGame.Static.SimulationFrameCounter + i);
                }
            });
        });
    }
    
    
    public static float GetAvgCpuLoad()
    {
        float sum = 0;
        int count;
        lock (CpuLoadLock)
        {
            count = CpuLoads.Count;
            for (var i = 0; i < count; i++)
            {
                sum += CpuLoads[i];
            }
        }

        return (float)Math.Round((decimal)(count > 0 ? sum / count : 0.0), 1);
    }
}