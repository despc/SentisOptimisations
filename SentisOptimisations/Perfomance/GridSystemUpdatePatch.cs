using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using NAPI;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using SentisOptimisations.DelayedLogic;
using Torch.Managers.PatchManager;
using VRage.Groups;

namespace SentisOptimisationsPlugin;

[PatchShim]
public static class GridSystemUpdatePatch
{
    
    private static PropertyInfo gridPropertyInfo =
        typeof(MyUpdateableGridSystem).GetProperty("Grid", BindingFlags.Instance | BindingFlags.NonPublic);
    
    private static ConcurrentDictionary<long, DateTime> GasUpdateTimes = new ConcurrentDictionary<long, DateTime>();
    private static ConcurrentDictionary<long, DateTime> ConveyorUpdateTimes = new ConcurrentDictionary<long, DateTime>();
    private static ConcurrentDictionary<long, DateTime> FlagForRecomputationPatchedTimes = new ConcurrentDictionary<long, DateTime>();

    public static void Patch(PatchContext ctx)
    {
        var MethodScheduleUpdateGas = typeof(MyGridGasSystem).GetMethod
            ("ScheduleUpdate", BindingFlags.Instance | BindingFlags.NonPublic);


        ctx.GetPattern(MethodScheduleUpdateGas).Prefixes.Add(
            typeof(GridSystemUpdatePatch).GetMethod(nameof(ScheduleUpdateGasPatched),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

        var MethodUpdateLines = typeof(MyGridConveyorSystem).GetMethod
            (nameof(MyGridConveyorSystem.UpdateLines), BindingFlags.Instance | BindingFlags.Public);


        ctx.GetPattern(MethodUpdateLines).Prefixes.Add(
            typeof(GridSystemUpdatePatch).GetMethod(nameof(MethodUpdateLinesPatched),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        
        var MethodFlagForRecomputation = typeof(MyGridConveyorSystem).GetMethod
            (nameof(MyGridConveyorSystem.FlagForRecomputation), BindingFlags.Instance | BindingFlags.Public);


        ctx.GetPattern(MethodFlagForRecomputation).Prefixes.Add(
            typeof(GridSystemUpdatePatch).GetMethod(nameof(FlagForRecomputationPatched),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
    }


    private static bool ScheduleUpdateGasPatched(MyGridGasSystem __instance)
    {
        if (!SentisOptimisationsPlugin.Config.GridSystemOptimisations)
        {
            return true;
        }

        MyCubeGrid grid = (MyCubeGrid)gridPropertyInfo.GetValue(__instance);

        var callScheduleTime = DateTime.Now;
        GasUpdateTimes[grid.EntityId] = callScheduleTime;
        DelayedProcessor.Instance.AddDelayedAction(DateTime.Now.AddSeconds(5), () =>
        {
            if (GasUpdateTimes.TryGetValue(grid.EntityId, out var lastGasUpdateTime))
            {
                if (lastGasUpdateTime > callScheduleTime)
                {
                    return;
                }

                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    try
                    {
                        __instance.easyCallMethod("Schedule", new object[] { });
                    }
                    catch
                    {
                    }
                });
            }
        });
        return false;
    }

    private static bool MethodUpdateLinesPatched(MyGridConveyorSystem __instance)
    {
        if (!SentisOptimisationsPlugin.Config.GridSystemOptimisations)
        {
            return true;
        }

        MyCubeGrid grid = (MyCubeGrid)gridPropertyInfo.GetValue(__instance);

        var callScheduleTime = DateTime.Now;
        ConveyorUpdateTimes[grid.EntityId] = callScheduleTime;
        DelayedProcessor.Instance.AddDelayedAction(DateTime.Now.AddSeconds(5), () =>
        {
            if (ConveyorUpdateTimes.TryGetValue(grid.EntityId, out var lastLinesUpdateTime))
            {
                if (lastLinesUpdateTime > callScheduleTime)
                {
                    return;
                }

                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    try
                    {
                        __instance.easyCallMethod("Schedule", new object[] { });
                        __instance.NeedsUpdateLines = true;
                    }
                    catch
                    {
                    }
                });
            }
        });
        return false;
    }
    
    private static bool FlagForRecomputationPatched(MyGridConveyorSystem __instance)
    {
        if (!SentisOptimisationsPlugin.Config.GridSystemOptimisations)
        {
            return true;
        }
        StackTrace stackTrace = new StackTrace();
        var stackFrames = stackTrace.GetFrames();
        if (stackFrames == null)
        {
            return true;
        }
        foreach (var stackFrame in stackFrames)
        {
            if (stackFrame.GetMethod().GetType() == typeof(MyShipController))
            {
                return true;
            }
        }
        
        MyCubeGrid grid = (MyCubeGrid)gridPropertyInfo.GetValue(__instance);

        var callScheduleTime = DateTime.Now;
        FlagForRecomputationPatchedTimes[grid.EntityId] = callScheduleTime;
        DelayedProcessor.Instance.AddDelayedAction(DateTime.Now.AddSeconds(5), () =>
        {
            if (FlagForRecomputationPatchedTimes.TryGetValue(grid.EntityId, out var lastLinesUpdateTime))
            {
                if (lastLinesUpdateTime > callScheduleTime)
                {
                    return;
                }

                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    try
                    {
                        MyGroups<MyCubeGrid, MyGridPhysicalHierarchyData>.Group group =
                            MyGridPhysicalHierarchy.Static.GetGroup(grid);
                        if (group == null)
                            return;
                        foreach (MyGroups<MyCubeGrid, MyGridPhysicalHierarchyData>.Node node in group.Nodes)
                            node.NodeData.GridSystems.ConveyorSystem.easySetField("m_needsRecomputation", true);
                    }
                    catch
                    {
                    }
                }); 
            }
            
        });
        return false;
    }
}