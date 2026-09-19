using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Sandbox;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// A wheel suspension recomputes its power draw every tenth frame instead of every frame.
    ///
    /// MyMotorSuspension.Update (every frame for every suspension of a moving vehicle) ends with
    /// ResourceSink.Update(). The draw it computes ramps toward the propulsion target by a step per
    /// call and drops back whenever the wheel is at its speed limit, so it changes nearly every
    /// frame, and every change marks the vehicle's power network for a full redistribution - all
    /// batteries, all consumers - in the next frame. With 64 six-wheeled vehicles that was ~13% of
    /// the game thread (distribution, sink updates, battery output events).
    ///
    /// All suspensions of one grid now update their draw in the same frame, every
    /// <see cref="PeriodFrames"/> frames (spread over grids by entity id), so a vehicle's network
    /// is redistributed at most once per ten frames. Between updates the draw keeps its last value.
    /// The ramp is a step per update, so it now takes ten times longer in game time (a quarter of a
    /// second became a few seconds) - that only shifts a little of the energy drawn at the start
    /// and end of acceleration. Driving itself does not read the draw.
    /// </summary>
    [PatchShim]
    public static class SuspensionPower
    {
        public const int PeriodFrames = 10;

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("SuspensionPower", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var update = typeof(MyMotorSuspension).GetMethod(nameof(MyMotorSuspension.Update),
                BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            ctx.GetPattern(update).Transpilers.Add(typeof(SuspensionPower).GetMethod(nameof(UpdateTranspiler),
                BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary><c>base.ResourceSink.Update()</c> becomes <c>UpdateSink(base.ResourceSink, this)</c>.</summary>
        private static IEnumerable<MsilInstruction> UpdateTranspiler(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            var sinkUpdate = typeof(MyResourceSinkComponent).GetMethod(nameof(MyResourceSinkComponent.Update), Type.EmptyTypes);
            var at = list.FindIndex(i => i.OpCode == OpCodes.Callvirt && i.Operand is MsilOperandInline<MethodBase> m && m.Value == sinkUpdate);
            if (at < 0 || list.FindIndex(at + 1, i => i.Operand is MsilOperandInline<MethodBase> m && m.Value == sinkUpdate) >= 0)
                throw new InvalidOperationException("SuspensionPower: expected one ResourceSink.Update call in MyMotorSuspension.Update");
            var labels = list[at].Labels.ToList();
            var loadThis = new MsilInstruction(OpCodes.Ldarg_0);
            foreach (var label in labels) loadThis.Labels.Add(label);
            list[at] = new MsilInstruction(OpCodes.Call).InlineValue(
                (MethodBase)typeof(SuspensionPower).GetMethod(nameof(UpdateSink), BindingFlags.Static | BindingFlags.Public));
            list.Insert(at, loadThis);
            return list;
        }

        public static void UpdateSink(MyResourceSinkComponent sink, MyMotorSuspension suspension)
        {
            var grid = suspension.CubeGrid;
            var frame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            if (grid == null || (frame + (ulong)grid.EntityId) % PeriodFrames == 0)
                sink.Update();
        }
    }
}
