using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.Game.Components;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using NAPI;

namespace SentisOptimisationsPlugin.Freezer;

/// <summary>
/// A thawed grid's refineries and assemblers work off the time they were frozen in steps of
/// <see cref="StepSeconds"/>, the way they would have worked it had they not been frozen - and the grid stays
/// awake until they are done.
///
/// Handing a block its frozen frames in one go (the timer's frames since the last trigger) lost most of the
/// production: a refinery refines only the ore already in its input, and it takes one pull of ore a tick; an
/// assembler takes the ingots for five seconds of its queue a tick - and on a grid that has just thawed, the
/// ingots the refinery is only about to make are not there yet. Measured with a grid asleep for 12 minutes against
/// one that kept running (SentisTests freeze_long_production): 55% of the ore refined, 1% of the plates built.
///
/// So each step does what the conveyor would have done over those seconds first, then the production pass:
///  * a refinery takes the ore it can refine in the step (at its best blueprint's rate: no more, so the others on
///    the network are not starved), as far as the network has it and its input holds it;
///  * an assembler takes what the part of its queue that fits in the step needs;
///  * then the block's own production pass for the step's frames, and what came out is pushed on, as vanilla does.
/// All the refineries of a group step before its assemblers, so the ingots of a step reach the assemblers in it.
/// Steps run at the end of frames within <see cref="BudgetMs"/>: twelve minutes are 36 steps a block, well inside the
/// seconds a grid is awake when it is woken.
/// </summary>
public static class CompensationCatchUp
{
    public const double StepSeconds = 20;
    public const uint StepFrames = (uint)(StepSeconds * 60);
    private const double BudgetMs = 2;

    private sealed class Job
    {
        public long Key;
        public int StartAt;
        public readonly List<MyCubeBlock> Blocks = new List<MyCubeBlock>();
        public readonly HashSet<long> Grids = new HashSet<long>();
        public readonly Dictionary<long, int> WaitingSince = new Dictionary<long, int>();
        public readonly Dictionary<long, int> PowerWaitSince = new Dictionary<long, int>();
        public readonly List<string> Dropped = new List<string>();
        public int Steps, Frames;
        public double Refined, LastPulled;
        public readonly Dictionary<long, (string Name, int Steps, uint Frames)> Done = new Dictionary<long, (string, int, uint)>();

        public void Count(MyCubeBlock block, uint frames)
        {
            Done.TryGetValue(block.EntityId, out var had);
            Done[block.EntityId] = (block.DisplayNameText, had.Steps + 1, had.Frames + frames);
        }
        public string Name;
    }

    // game thread
    private static readonly Dictionary<long, Job> Jobs = new Dictionary<long, Job>();
    private static bool _scheduled;

    // read by the freezer loop: a grid with frames still owed is not frozen again
    private static readonly ConcurrentDictionary<long, byte> Busy = new ConcurrentDictionary<long, byte>();

    public static bool IsBusy(IEnumerable<MyCubeGrid> grids) => !Busy.IsEmpty && grids.Any(g => Busy.ContainsKey(g.EntityId));

    private static readonly MethodInfo UpdateProduction = typeof(MyRefinery)
        .GetMethod("UpdateProduction", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(uint) }, null);
    private static readonly MethodInfo UpdateProductionAssembler = typeof(MyAssembler)
        .GetMethod("UpdateProduction", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>Game thread, on the thaw: a block that is owed frames.</summary>
    public static void Add(MyCubeGrid grid, MyCubeBlock block, int startAt)
    {
        var key = MyCubeGridGroups.Static.Logical.GetGroupNodes(grid).Min(g => g.EntityId);
        if (!Jobs.TryGetValue(key, out var job)) Jobs[key] = job = new Job { Key = key, StartAt = startAt, Name = grid.DisplayName };
        if (!job.Blocks.Contains(block)) job.Blocks.Add(block);
        job.Grids.Add(grid.EntityId);
        Busy[grid.EntityId] = 0;
        if (_scheduled) return;
        _scheduled = true;
        MyAPIGateway.Utilities.InvokeOnGameThread(Tick, "CompensationCatchUp", startAt);
    }

    private static uint Owed(MyCubeBlock block) => CompensationTracker.PeekPending(block.EntityId) ?? 0;

    private static void Tick()
    {
        _scheduled = false;
        var frame = (int)MySandboxGame.Static.SimulationFrameCounter;
        var watch = Stopwatch.StartNew();
        foreach (var job in Jobs.Values.ToList())
        {
            if (frame < job.StartAt) continue;
            try
            {
                job.Blocks.RemoveAll(b =>
                {
                    if (b.Closed || b.MarkedForClose) { CompensationTracker.Forget(b.EntityId); return true; }
                    return CompensationTracker.IsFrozen(b.EntityId) || Owed(b) == 0;
                });
                while (job.Blocks.Count > 0 && watch.Elapsed.TotalMilliseconds < BudgetMs)
                {
                    var before = job.Steps;
                    Step(job);
                    if (job.Steps == before) break;             // all of them waiting for power: next frame
                    job.Blocks.RemoveAll(b => b.Closed || b.MarkedForClose || Owed(b) == 0);
                }
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "Compensation catch-up failed; the rest of it is dropped");
                foreach (var b in job.Blocks) CompensationTracker.CancelPending(b.EntityId);
                job.Blocks.Clear();
            }
            if (job.Blocks.Count > 0) continue;
            Jobs.Remove(job.Key);
            FreezeLogic.CompensationLogs($"Compensation catch-up on grid '{job.Name}' in {frame - job.StartAt} frames: " +
                                         string.Join(", ", job.Done.Values.Select(d => $"'{d.Name}' {d.Frames / 60.0:0} s in {d.Steps} steps")) +
                                         $"; {job.Refined:F0} kg of ore refined" + (job.Dropped.Count > 0 ? "; dropped: " + string.Join(", ", job.Dropped) : ""));
            foreach (var id in job.Grids)
                if (!Jobs.Values.Any(j => j.Grids.Contains(id))) Busy.TryRemove(id, out _);
        }
        if (Jobs.Count == 0 || _scheduled) return;
        _scheduled = true;
        MyAPIGateway.Utilities.InvokeOnGameThread(Tick, "CompensationCatchUp", frame + 1);
    }

    /// <summary>How long a thawed block that is on but has no power yet (a reactor still taking its fuel) is waited for.</summary>
    private const int PowerWaitFrames = 10 * 60;

    /// <summary>
    /// Whether the block can work a step now. Switched off or broken: nothing is owed, the debt goes. On but not
    /// working (no power yet): it is waited for - up to <see cref="PowerWaitFrames"/>, then the debt goes too.
    /// </summary>
    private static bool Ready(Job job, MyFunctionalBlock block)
    {
        if (block.IsWorking)
        {
            job.WaitingSince.Remove(block.EntityId);
            return true;
        }
        var frame = (int)MySandboxGame.Static.SimulationFrameCounter;
        if (!block.Enabled || !block.IsFunctional)
        {
            job.Dropped.Add($"'{block.DisplayNameText}' switched off");
            CompensationTracker.CancelPending(block.EntityId);
            return false;
        }
        if (!job.WaitingSince.TryGetValue(block.EntityId, out var since)) job.WaitingSince[block.EntityId] = since = frame;
        if (frame - since < PowerWaitFrames) return false;
        job.Dropped.Add($"'{block.DisplayNameText}' without power");
        CompensationTracker.CancelPending(block.EntityId);
        return false;
    }

    private static readonly Dictionary<Type, MethodInfo> OperationalPower = new Dictionary<Type, MethodInfo>();

    /// <summary>
    /// The power a production block draws when it works, asked for before the step and waited for: a block that
    /// stood idle asks only for its standby power, and the distributor hands out a new demand at its next update -
    /// a pass run before that finds no power and works nothing (that was every step of a thawed idle refinery).
    /// </summary>
    private static bool Powered(Job job, MyProductionBlock block)
    {
        var type = block.GetType();
        if (!OperationalPower.TryGetValue(type, out var method))
        {
            for (var t = type; t != null && method == null; t = t.BaseType)
                method = t.GetMethod("GetOperationalPowerConsumption", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            OperationalPower[type] = method;
        }
        if (method == null) return true;
        var operational = (float)method.Invoke(block, null);
        var electricity = Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId;
        var sink = block.ResourceSink;
        if (sink == null) return true;
        if (sink.RequiredInputByType(electricity) != operational) sink.SetRequiredInputByType(electricity, operational);
        if (sink.IsPoweredByType(electricity) && sink.CurrentInputByType(electricity) >= operational * 0.999f)
        {
            job.PowerWaitSince.Remove(block.EntityId);
            return true;
        }
        // waited for like a block that has no power yet at all (Ready): up to PowerWaitFrames, then the debt goes
        var frame = (int)MySandboxGame.Static.SimulationFrameCounter;
        if (!job.PowerWaitSince.TryGetValue(block.EntityId, out var since)) job.PowerWaitSince[block.EntityId] = since = frame;
        if (frame - since < PowerWaitFrames) return false;
        job.Dropped.Add($"'{block.DisplayNameText}' short of power ({sink.CurrentInputByType(electricity):F3} of {operational:F3} MW)");
        CompensationTracker.CancelPending(block.EntityId);
        return false;
    }

    private static void Step(Job job)
    {
        foreach (var block in job.Blocks.OfType<MyRefinery>().ToList())
            if (Ready(job, block))
            {
                var ore = Ore(block);
                if (!RefineryStep(job, block)) continue;
                var after = Ore(block) - job.LastPulled;
                job.Refined += ore - after;
                job.Steps++;
            }
        foreach (var block in job.Blocks.OfType<MyAssembler>().ToList())
            if (Ready(job, block) && AssemblerStep(job, block)) job.Steps++;
        // anything else the freezer compensates: its frames as they are
        foreach (var block in job.Blocks.Where(b => !(b is MyRefinery) && !(b is MyAssembler)).ToList())
            if (CompensationTracker.TryTakeCompensation(block.EntityId, uint.MaxValue, out var frames))
            {
                GiveToTimer(block, frames);
                job.Count(block, frames);
            }
    }

    private static double Ore(MyRefinery refinery) =>
        refinery.InputInventory.GetItems().Where(i => i.Content is MyObjectBuilder_Ore).Sum(i => (double)i.Amount);

    private static bool RefineryStep(Job job, MyRefinery refinery)
    {
        job.LastPulled = 0;
        var input = refinery.InputInventory;
        if (refinery.UseConveyorSystem && input != null)
        {
            var need = OrePerSecond(refinery) * Math.Min(StepFrames, Owed(refinery)) / 60.0 * 1.1 - Ore(refinery);
            // ore by ore, the conveyor path worked out now (PullItems skips a source whose path is not known yet
            // and only starts working it out - and just after a thaw none is, while the whole catch-up takes a
            // frame or two)
            if (need > 1 && input.Constraint != null)
                foreach (var id in input.Constraint.ConstrainedIds)
                {
                    if (need <= 1 || input.VolumeFillFactor >= 0.99f) break;
                    var pulled = (double)refinery.CubeGrid.GridSystems.ConveyorSystem.PullItem(id, (MyFixedPoint)need, refinery, input, false, true);
                    need -= pulled;
                    job.LastPulled += pulled;
                }
        }
        if (Ore(refinery) > 0 && !Powered(job, refinery)) return false;
        if (!CompensationTracker.TryTakeCompensation(refinery.EntityId, StepFrames, out var frames)) return false;
        job.Count(refinery, frames);
        UpdateProduction.Invoke(refinery, new object[] { frames });
        if (refinery.UseConveyorSystem && refinery.OutputInventory.VolumeFillFactor > 0.25f)
            MyGridConveyorSystem.PushAnyRequest(refinery, refinery.OutputInventory);
        return true;
    }

    private static bool AssemblerStep(Job job, MyAssembler assembler)
    {
        if (assembler.UseConveyorSystem && !assembler.DisassembleEnabled)
            PullForQueue(assembler, Math.Min(StepFrames, Owed(assembler)) / 60f);
        if (!assembler.IsQueueEmpty && !Powered(job, assembler)) return false;
        if (!CompensationTracker.TryTakeCompensation(assembler.EntityId, StepFrames, out var frames)) return false;
        job.Count(assembler, frames);
        UpdateProductionAssembler.Invoke(assembler, new object[] { frames, false });
        if (assembler.UseConveyorSystem && assembler.OutputInventory.VolumeFillFactor > 0.25f)
            MyGridConveyorSystem.PushAnyRequest(assembler, assembler.OutputInventory);
        return true;
    }

    /// <summary>
    /// What vanilla's pull does for five seconds of the queue, for <paramref name="seconds"/>: the queue in order,
    /// each entry as many items as fit in the time left, what they need pulled in. An entry whose parts the network
    /// does not have takes no time, so the entries after it get it (vanilla gives the time back the same way).
    /// </summary>
    private static void PullForQueue(MyAssembler assembler, float seconds)
    {
        var speed = MySession.Static.AssemblerSpeedMultiplier *
                    (((MyAssemblerDefinition)assembler.BlockDefinition).AssemblySpeed + assembler.UpgradeValues["Productivity"]);
        var input = assembler.InputInventory;
        var used = 0f;
        var wanted = new Dictionary<MyDefinitionId, MyFixedPoint>();     // what the entries so far keep in the input
        foreach (var item in assembler.Queue.ToList())
        {
            if (used >= seconds) break;
            var each = item.Blueprint.BaseProductionTimeInSeconds / speed;
            var count = Math.Max(1, Math.Min((int)item.Amount, (int)Math.Ceiling((seconds - used) / each)));
            var multiplier = (MyFixedPoint)(1f / assembler.GetEfficiencyMultiplierForBlueprint(item.Blueprint));
            var buildable = true;
            foreach (var prerequisite in item.Blueprint.Prerequisites)
            {
                wanted.TryGetValue(prerequisite.Id, out var before);
                var total = before + prerequisite.Amount * multiplier * count;
                var missing = total - input.GetItemAmount(prerequisite.Id);
                if (missing > 0)
                    assembler.CubeGrid.GridSystems.ConveyorSystem.PullItem(prerequisite.Id, missing, assembler, input, false, true);     // the path now: the whole catch-up takes a second or two
                wanted[prerequisite.Id] = total;
                // not even one item's worth of it: this entry is skipped by the pass as well
                if (input.GetItemAmount(prerequisite.Id) < before + prerequisite.Amount * multiplier) buildable = false;
            }
            if (buildable) used += count * each;
        }
    }

    private static readonly Dictionary<(MyDefinitionId, float), double> OreRates = new Dictionary<(MyDefinitionId, float), double>();

    /// <summary>The most ore a refinery gets through in a second, over its blueprints (the formula of its pass).</summary>
    public static double OrePerSecond(MyRefinery refinery)
    {
        var definition = (MyRefineryDefinition)refinery.BlockDefinition;
        var productivity = refinery.UpgradeValues["Productivity"];
        var key = (definition.Id, productivity + MySession.Static.RefinerySpeedMultiplier * 1000);
        if (OreRates.TryGetValue(key, out var rate)) return rate;
        rate = 0;
        foreach (var blueprintClass in definition.BlueprintClasses)
        foreach (var blueprint in blueprintClass)
        {
            if (blueprint.BaseProductionTimeInSeconds <= 0) continue;
            var perSecond = (definition.RefineSpeed + productivity) * MySession.Static.RefinerySpeedMultiplier / blueprint.BaseProductionTimeInSeconds;
            foreach (var prerequisite in blueprint.Prerequisites)
                if (prerequisite.Id.TypeId == typeof(MyObjectBuilder_Ore))
                    rate = Math.Max(rate, perSecond * (double)prerequisite.Amount);
        }
        return OreRates[key] = rate;
    }

    private static void GiveToTimer(MyCubeBlock block, uint frames)
    {
        var timer = (MyTimerComponent)block.easyGetField("m_timer", typeof(MyFunctionalBlock));
        if (timer == null) return;
        var vanilla = timer.FramesFromLastTrigger;
        timer.FramesFromLastTrigger = uint.MaxValue - vanilla < frames ? uint.MaxValue : vanilla + frames;
    }

    public static void Clear()
    {
        Jobs.Clear();
        Busy.Clear();
        _scheduled = false;
    }
}
