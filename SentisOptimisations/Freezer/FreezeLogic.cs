using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using SentisOptimisations;
using VRage.Game.Entity;
using VRage.Game.Entity.EntityComponents.Interfaces;
using VRageMath;

namespace SentisOptimisationsPlugin.Freezer;

public class FreezeLogic
{
    private static int _wakeupTimeInSec = 5;
    public static HashSet<long> FrozenGrids = new();
    private Dictionary<long, DateTime> WakeUpDatas = new(); //EntityId:NextWakeUpTime
    public static Dictionary<long, ulong> LastUpdateFrames = new(); //EntityId:NextWakeUpTime

    public void CheckGridGroup(HashSet<MyCubeGrid> grids)
    {
        var anyGrid = grids.FirstElement();
        var gridsPosition = anyGrid.PositionComp.GetPosition();
        Thread.Sleep(16);
        if (PlayerUtils.IsAnyPlayersInRadius(gridsPosition, SentisOptimisationsPlugin.Config.FreezeDistance)
            || IsWakeUpTime(grids))
        {
            UnfreezeGrids(grids);
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

    private void UnfreezeGrids(HashSet<MyCubeGrid> grids)
    {
        foreach (var grid in grids)
        {
            if (!FrozenGrids.Contains(grid.EntityId))
            {
                continue;
            }

            if (grid.Parent == null && !grid.IsPreview)
            {
                Log("Unfreeze grid " + grid.DisplayName);
                FrozenGrids.Remove(grid.EntityId);
                MyAPIGateway.Utilities.InvokeOnGameThread(() => { RegisterRecursive(grid); });
            }
        }
    }

    private void FreezeGrids(HashSet<MyCubeGrid> grids)
    {
        var antifreezeBlocksSubtypes = SentisOptimisationsPlugin.Config.AntifreezeBlocksSubtypes.Split(';');
        foreach (var grid in grids)
        {
            if (grid.GetBlocks().Any(block =>
                    Enumerable.Contains(antifreezeBlocksSubtypes, block.BlockDefinition.Id.SubtypeName)))
            {
                // Log("Found antifreeze block, skip grid " + grid.DisplayName);
                return;
            }

            if (!SentisOptimisationsPlugin.Config.FreezeSignals && grid.DisplayName.Contains("Container MK-"))
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
        }
        else
        {
            WakeUpDatas.Add(minEntityId,
                DateTime.Now + TimeSpan.FromSeconds(SentisOptimisationsPlugin.Config.MinWakeUpIntervalInSec +
                                                    minEntityId % SentisOptimisationsPlugin.Config
                                                        .MinWakeUpIntervalInSec));
        }

        foreach (var grid in grids)
        {
            if (FrozenGrids.Contains(grid.EntityId))
            {
                continue;
            }

            if (grid.Parent == null && !grid.IsPreview)
            {
                Log("Freeze grid " + grid.DisplayName);
                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    if (!grid.IsStatic)
                    {
                        grid.Physics?.SetSpeeds(Vector3.Zero, Vector3.Zero);
                    }

                    LastUpdateFrames[grid.EntityId] = MySandboxGame.Static.SimulationFrameCounter;
                    FrozenGrids.Add(grid.EntityId);
                    
                    UnregisterRecursive(grid);
                });
            }
        }
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


    private void Log(string message)
    {
        if (SentisOptimisationsPlugin.Config.EnableDebugLogs)
        {
            SentisOptimisationsPlugin.Log.Warn(message);
        }
    }
}