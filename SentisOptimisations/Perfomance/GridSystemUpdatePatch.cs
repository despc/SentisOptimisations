using System;
using System.Collections.Concurrent;
using System.Reflection;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using SentisOptimisations.DelayedLogic;
using Torch.Managers.PatchManager;
using VRage.Game.ModAPI;
using VRage.Groups;

namespace SentisOptimisationsPlugin;

/// <summary>
/// Grid systems that rebuild themselves on every change rebuild once the changes stop.
///
/// Building a conveyor network or a gas system walks every block of the grid, and the game asks for
/// it on every block placed, removed or reconnected - welding a ship rebuilds it hundreds of times.
/// Each request is held for <see cref="DebounceSeconds"/> instead, and the rebuild happens when
/// nothing has asked for it during that time.
///
/// The grid keeps working in the meantime: only the rebuild is postponed, and a request that arrives
/// while one is pending simply pushes it further out.
/// </summary>
[PatchShim]
public static class GridSystemUpdatePatch
{
    /// <summary>How long the last request is waited out before the rebuild runs.</summary>
    private const double DebounceSeconds = 5;

    private static readonly PropertyInfo GridProperty =
        typeof(MyUpdateableGridSystem).GetProperty("Grid", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly Action<MyGridGasSystem> ScheduleGas =
        Accessors.Method<MyGridGasSystem, Action<MyGridGasSystem>>("Schedule");
    private static readonly Action<MyGridConveyorSystem> ScheduleConveyor =
        Accessors.Method<MyGridConveyorSystem, Action<MyGridConveyorSystem>>("Schedule");
    private static readonly Action<MyGridConveyorSystem, bool> SetNeedsRecomputation =
        Accessors.SetField<MyGridConveyorSystem, bool>("m_needsRecomputation");

    private static readonly ConcurrentDictionary<long, long> GasRequests = new ConcurrentDictionary<long, long>();
    private static readonly ConcurrentDictionary<long, long> ConveyorRequests = new ConcurrentDictionary<long, long>();
    private static readonly ConcurrentDictionary<long, long> RecomputeRequests = new ConcurrentDictionary<long, long>();

    private static long _sequence;

    public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("GridSystemUpdatePatch", ctx, PatchImpl);

    internal static void PatchImpl(PatchContext ctx)
    {
        if (GridProperty == null) throw new MissingMemberException("MyUpdateableGridSystem.Grid");
        if (ScheduleGas == null || ScheduleConveyor == null || SetNeedsRecomputation == null)
            throw new InvalidOperationException("GridSystemUpdatePatch: a grid system member could not be bound");

        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags publicInstance = BindingFlags.Instance | BindingFlags.Public;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var self = typeof(GridSystemUpdatePatch);

        ctx.GetPattern(typeof(MyGridGasSystem).GetMethod("ScheduleUpdate", instance))
            .Prefixes.Add(self.GetMethod(nameof(ScheduleUpdateGasPatched), statics));
        ctx.GetPattern(typeof(MyGridConveyorSystem).GetMethod(nameof(MyGridConveyorSystem.UpdateLines), publicInstance))
            .Prefixes.Add(self.GetMethod(nameof(UpdateLinesPatched), statics));
        ctx.GetPattern(typeof(MyGridConveyorSystem).GetMethod(nameof(MyGridConveyorSystem.FlagForRecomputation), publicInstance))
            .Prefixes.Add(self.GetMethod(nameof(FlagForRecomputationPatched), statics));
    }

    /// <summary>Forgets what a grid asked for; it is gone.</summary>
    public static void CleanupEntity(VRage.Game.Entity.MyEntity entity)
    {
        if (!(entity is MyCubeGrid grid)) return;
        GasRequests.TryRemove(grid.EntityId, out _);
        ConveyorRequests.TryRemove(grid.EntityId, out _);
        RecomputeRequests.TryRemove(grid.EntityId, out _);
    }

    private static bool ScheduleUpdateGasPatched(MyGridGasSystem __instance) =>
        !Debounce(GasRequests, __instance, grid => ScheduleGas(__instance));

    private static bool UpdateLinesPatched(MyGridConveyorSystem __instance) =>
        !Debounce(ConveyorRequests, __instance, grid =>
        {
            ScheduleConveyor(__instance);
            __instance.NeedsUpdateLines = true;
        });

    private static bool FlagForRecomputationPatched(MyGridConveyorSystem __instance) =>
        !Debounce(RecomputeRequests, __instance, grid =>
        {
            // The whole physical group shares a conveyor network, so all of it is flagged at once.
            var group = MyGridPhysicalHierarchy.Static?.GetGroup(grid);
            if (group == null)
            {
                SetNeedsRecomputation(__instance, true);
                return;
            }

            foreach (MyGroups<MyCubeGrid, MyGridPhysicalHierarchyData>.Node node in group.Nodes)
            {
                var conveyors = node.NodeData?.GridSystems?.ConveyorSystem;
                if (conveyors != null) SetNeedsRecomputation(conveyors, true);
            }
        });

    /// <summary>
    /// Holds the request for <see cref="DebounceSeconds"/> and runs <paramref name="rebuild"/> on the
    /// game thread if nothing asked again in the meantime. True when the request was taken over,
    /// false when the caller should do it itself (the feature is off, or the grid is unknown).
    /// </summary>
    private static bool Debounce(ConcurrentDictionary<long, long> requests, MyUpdateableGridSystem system, Action<MyCubeGrid> rebuild)
    {
        if (!SentisOptimisationsPlugin.Config.GridSystemOptimisations) return false;

        MyCubeGrid grid;
        try
        {
            grid = (MyCubeGrid)GridProperty.GetValue(system);
        }
        catch (Exception e)
        {
            SentisOptimisationsPlugin.Log.Error(e, "Grid system debounce could not read its grid");
            return false;
        }

        if (grid == null || grid.MarkedForClose) return false;

        // A counter, not a clock: two requests inside the same tick of DateTime.Now compared equal,
        // and the second one then cancelled the first.
        var ticket = System.Threading.Interlocked.Increment(ref _sequence);
        var gridId = grid.EntityId;
        requests[gridId] = ticket;

        DelayedProcessor.Instance.AddDelayedAction(DateTime.Now.AddSeconds(DebounceSeconds), () =>
        {
            if (!requests.TryGetValue(gridId, out var latest) || latest != ticket) return;
            MyAPIGateway.Utilities.InvokeOnGameThread(() =>
            {
                try
                {
                    if (!requests.TryRemove(gridId, out var current) || current != ticket) return;
                    if (grid.MarkedForClose || grid.Closed) return;
                    rebuild(grid);
                }
                catch (Exception e)
                {
                    SentisOptimisationsPlugin.Log.Error(e, "Delayed grid system rebuild failed");
                }
            });
        });
        return true;
    }
}
