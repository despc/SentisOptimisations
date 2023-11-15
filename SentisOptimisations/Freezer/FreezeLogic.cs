using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAPI;
using Sandbox;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using SentisOptimisations;
using VRage.Game.Entity;
using VRage.Game.Entity.EntityComponents.Interfaces;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisOptimisationsPlugin.Freezer;

public class FreezeLogic
{
    private static int _wakeupTimeInSec = 10;
    public static HashSet<long> FrozenGrids = new();
    public static HashSet<long> InFreezeQueue = new();
    private Dictionary<long, DateTime> WakeUpDatas = new(); //EntityId:NextWakeUpTime
    public static Dictionary<long, ulong> LastUpdateFrames = new(); //BlockId:LastUpdateFrame
    public static List<float> CpuLoads = new();

    public void CheckGridGroup(HashSet<MyCubeGrid> grids)
    {
        var anyGrid = grids.FirstElement();
        var gridsPosition = anyGrid.PositionComp.GetPosition();
        Thread.Sleep(16);
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

                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
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

            if (!SentisOptimisationsPlugin.Config.FreezeSignals && (grid.DisplayName.Contains("Container MK-") || grid.DisplayName.Contains("Container_MK-")))
            {
                return;
            }

            if (!SentisOptimisationsPlugin.Config.FreezeNpc && grid.isNpcGrid())
            {
                // Log("Dont freeze NPC, skip grid " + grid.DisplayName);
                return;
            }
        }

        var minEntityId = grids.MinBy(grid => grid.EntityId).EntityId;
        if (WakeUpDatas.TryGetValue(minEntityId, out var dateTime))
        {
            if (DateTime.Now.AddSeconds(_wakeupTimeInSec) > dateTime)
            {
                WakeUpDatas[minEntityId] = DateTime.Now +
                                           TimeSpan.FromSeconds(
                                               SentisOptimisationsPlugin.Config.MinWakeUpIntervalInSec +
                                               minEntityId % SentisOptimisationsPlugin.Config.MinWakeUpIntervalInSec);
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
        lock (InFreezeQueue)
        {
            if (InFreezeQueue.Contains(minEntityId))
            {
                return;
            }
            InFreezeQueue.Add(minEntityId);
        }
        
        Task.Run(() =>
        {
            Thread.Sleep(SentisOptimisationsPlugin.Config.DelayBeforeFreezeSec * 1000);
            
            foreach (var grid in grids)
            {
                if (grid == null) continue;
                if (FrozenGrids.Contains(grid.EntityId))
                {
                    continue;
                }

                var isInQueue = false;
                lock (InFreezeQueue)
                {
                    isInQueue = InFreezeQueue.Contains(minEntityId); 
                }
                if (grid.Parent == null && !grid.IsPreview && isInQueue)
                {
                    Log("Freeze grid " + grid.DisplayName);
                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    {
                       
                        if (!grid.IsStatic)
                        {
                            grid.Physics?.SetSpeeds(Vector3.Zero, Vector3.Zero);
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

    public void UpdateCpuLoad(float cpuLoad)
    {
        if (CpuLoads.Count > 5)
        {
            CpuLoads.Remove(0);
        }
        CpuLoads.Add(cpuLoad);
    }
}