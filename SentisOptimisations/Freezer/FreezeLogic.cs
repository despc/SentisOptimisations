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
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using SentisOptimisations;
using SentisOptimisations.DelayedLogic;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.Entity.EntityComponents.Interfaces;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisOptimisationsPlugin.Freezer;

public class FreezeLogic
{
    private static int _wakeupTimeInSec = 10;
    public static HashSet<long> FrozenGrids = new();
    public static HashSet<long> FrozenPhysicsGrids = new();
    public static HashSet<long> InFreezeQueue = new();
    private Dictionary<long, DateTime> WakeUpDatas = new(); //EntityId:NextWakeUpTime
    public static ConcurrentDictionary<long, ulong> LastUpdateFrames = new(); //BlockId:LastUpdateFrame
    public static List<float> CpuLoads = new();
    Random random = new Random();

    public void CheckGridGroup(HashSet<MyCubeGrid> grids)
    {
        var anyGrid = grids.FirstElement();
        var gridsPosition = anyGrid.PositionComp.GetPosition();
        var isWakeUpTime = IsWakeUpTime(grids);
        if (PlayerUtils.IsAnyPlayersInRadius(gridsPosition, SentisOptimisationsPlugin.Config.FreezeDistance)
            || isWakeUpTime)
        {
            UnfreezeGrids(grids, isWakeUpTime);
            return;
        }

        if (!SentisOptimisationsPlugin.Config.FreezerEnabled)
        {
            return;
        }

        FreezeGrids(grids);
    }

    private bool IsWakeUpTime(HashSet<MyCubeGrid> grids)
    {
        var minEntityId = grids.MinBy(grid => grid.EntityId).EntityId;
        if (WakeUpDatas.TryGetValue(minEntityId, out var data))
        {
            if (DateTime.Now < data && data < DateTime.Now.AddSeconds(_wakeupTimeInSec))
            {
                // grids.ForEach(grid => Log("Wake up time, grid - " + grid.DisplayName));

                return true;
            }
        }

        return false;
    }

    private void UnfreezeGrids(HashSet<MyCubeGrid> grids, bool isWakeUpTime)
    {
        var minEntityId = grids.MinBy(grid => grid.EntityId).EntityId;
        lock (InFreezeQueue)
        {
            InFreezeQueue.Remove(minEntityId);
        }
        var groupWithFixedGrid = GroupContainsFixedGrid(grids);
        foreach (var grid in grids)
        {
            if (!FrozenGrids.Contains(grid.EntityId))
            {
                continue;
            }

            if (!isWakeUpTime)
            {
                WakeUpDatas.Remove(grid.EntityId);
            }

            if (grid.Parent == null && !grid.IsPreview)
            {
                Log("Unfreeze grid " + grid.DisplayName);
                FrozenGrids.Remove(grid.EntityId);
                lock (InFreezeQueue)
                {
                    InFreezeQueue.Remove(grid.EntityId);
                }
             
                CompensateFrozenFrames(grid);
                FrozenPhysicsGrids.Remove(grid.EntityId);
                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    if (!grid.IsStatic)
                    {
                        var gridPhysics = grid.Physics;

                        if (gridPhysics != null && SentisOptimisationsPlugin.Config.FreezePhysics && !groupWithFixedGrid)
                        {
                           DoUnfreezePhysics(grid);
                        }
                    }
                    RegisterRecursive(grid);
                    grid.PlayerPresenceTier = MyUpdateTiersPlayerPresence.Normal;
                });
            }
        }
    }

    private static void CompensateFrozenFrames(MyCubeGrid grid)
    {
        foreach (var myCubeBlock in grid.GetFatBlocks())
        {
            if (!(myCubeBlock is MyFunctionalBlock))
            {
                continue;
            }

            var needToCompensate = NeedToCompensate((MyFunctionalBlock)myCubeBlock);
            if (needToCompensate)
            {
                MyTimerComponent timer =
                    (MyTimerComponent)myCubeBlock.easyGetField("m_timer", typeof(MyFunctionalBlock));
                if (timer != null)
                {
                    if (LastUpdateFrames.TryGetValue(myCubeBlock.EntityId, out var lastUpdateFrame))
                    {
                        var framesAfterFreeze =
                            (uint)(MySandboxGame.Static.SimulationFrameCounter - lastUpdateFrame);
                        timer.FramesFromLastTrigger = framesAfterFreeze;
                        Log("Compensate " + framesAfterFreeze + " frozen frames of " +
                            myCubeBlock.DisplayNameText + " of grid " + grid.DisplayName);
                        LastUpdateFrames.Remove(myCubeBlock.EntityId);
                    }
                }
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
        
        lock (InFreezeQueue)
        {
            if (InFreezeQueue.Contains(minEntityId))
            {
                return;
            }

            InFreezeQueue.Add(minEntityId);
        }

        var delayBeforeFreezeSec = SentisOptimisationsPlugin.Config
            .DelayBeforeFreezeSec;
        var groupWithFixedGrid = GroupContainsFixedGrid(grids);
        var needToFreezeGrids = grids.Where(grid => !FrozenGrids.Contains(grid.EntityId)).ToList();

        if (needToFreezeGrids.Count == 0)
        {
            return;
        }
        Thread.Sleep(8);
        DelayedProcessor.Instance.AddDelayedAction(DateTime.Now.AddSeconds(delayBeforeFreezeSec), () =>
        {
            foreach (var grid in needToFreezeGrids)
            {
                if (grid == null || grid.Closed || grid.IsPreview) continue;
                if (FrozenGrids.Contains(grid.EntityId))
                {
                    continue;
                }

                var isInQueue = false;
                lock (InFreezeQueue)
                {
                    isInQueue = InFreezeQueue.Contains(minEntityId);
                }

                if (grid.Parent == null && isInQueue)
                {
                    Log("Freeze grid " + grid.DisplayName);
                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    {
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
                            }
                        }

                        FrozenGrids.Add(grid.EntityId);
                        
                        UnregisterRecursive(grid);
                    });
                }

                foreach (var myCubeBlock in grid.GetFatBlocks())
                {
                    if (!(myCubeBlock is MyFunctionalBlock))
                    {
                        continue;
                    }

                    var needToCompensate = NeedToCompensate((MyFunctionalBlock)myCubeBlock);
                    if (needToCompensate)
                    {
                        LastUpdateFrames[myCubeBlock.EntityId] = MySandboxGame.Static.SimulationFrameCounter;
                    }
                }
            }

            lock (InFreezeQueue)
            {
                InFreezeQueue.Remove(minEntityId);
            }
        });
    }

    private static bool GroupContainsFixedGrid(HashSet<MyCubeGrid> grids)
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
            return grid.Physics.Constraints.Any(constraint =>
            {
                if (constraint.RigidBodyB == null)
                {
                    return false;
                }
                return constraint.RigidBodyB.UserObject is MyVoxelPhysicsBody;
            });
        });
    }

    private static void DoFreezePhysics(MyCubeGrid grid)
    {
        var gridPhysics = grid.Physics;

        if (gridPhysics.Constraints.Any(constraint =>
                constraint.ConstraintData is HkFixedConstraintData && constraint.Enabled))
        {
            return;
        }
        
        gridPhysics.ConvertToStatic();
        grid.RaisePhysicsChanged();
        FrozenPhysicsGrids.Add(grid.EntityId);
        // gridPhysics.RigidBody.UpdateMotionType(HkMotionType.Fixed);
        // gridPhysics.RigidBody.Quality = HkCollidableQualityType.Fixed;
    }
    
    private static void DoUnfreezePhysics(MyCubeGrid grid)
    {
        var gridPhysics = grid.Physics;
        if (gridPhysics.Constraints.Any(constraint =>
                constraint.ConstraintData is HkFixedConstraintData && constraint.Enabled))
        {
            return;
        }
        
        gridPhysics.ConvertToDynamic(grid.GridSizeEnum == MyCubeSize.Large, grid.IsClientPredicted);
        gridPhysics.SetSpeeds(Vector3.Zero, Vector3.Zero);
        grid.RaisePhysicsChanged();
        grid.RecalculateGravity();
        
        // gridPhysics.RigidBody.UpdateMotionType(HkMotionType.Box_Inertia);
        // gridPhysics.RigidBody.Quality = HkCollidableQualityType.Moving;
    }

    public static bool NeedToCompensate(MyFunctionalBlock myCubeBlock)
    {
        var needToCompensate = myCubeBlock is MyProductionBlock
                               || myCubeBlock is MyGasGenerator
                               || myCubeBlock is MyFueledPowerProducer;
        return needToCompensate;
    }

    private void UnregisterRecursive(MyEntity e)
    {
        MyEntities.UnregisterForUpdate(e);
        (e.GameLogic as IMyGameLogicComponent)?.UnregisterForUpdate();
        if (e.Hierarchy == null) return;

        foreach (var child in e.Hierarchy.Children) UnregisterRecursive((MyEntity)child.Container.Entity);
    }

    private void RegisterRecursive(MyEntity e)
    {
        MyEntities.RegisterForUpdate(e);
        (e.GameLogic as IMyGameLogicComponent)?.RegisterForUpdate();
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
        while (CpuLoads.Count > 20)
        {
            CpuLoads.RemoveAt(0);
        }

        CpuLoads.Add(cpuLoad);
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

                foreach (var group in groups) {
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
                                    FrozenPhysicsGrids.Remove(myCubeGrid.EntityId);
                                    if (!groupWithFixedGrid)
                                    {
                                        gridPhysics.SetSpeeds(Vector3.Zero, Vector3.Zero);
                                        DoUnfreezePhysics((MyCubeGrid)myCubeGrid);
                                    }
                                }
                            }
                        }
                    }, StartAt: (int)MySandboxGame.Static.SimulationFrameCounter + i);
                }
            });
        });
    }
}