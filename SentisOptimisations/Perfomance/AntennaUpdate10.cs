using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using VRage.Game.ModAPI;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// An antenna looks around for other antennas three times less often, and ten times less often on a
    /// grid no player sees.
    ///
    /// Every working antenna, every 10 frames, asks the world which antennas reach it and which ones
    /// it reaches (<c>MyRadioAntenna.UpdateAfterSimulation10</c> - <c>RadioReceiver.UpdateBroadcastersInRange</c>),
    /// two sphere queries over all broadcasters plus the antenna system's lists: the cost grows with
    /// the square of the antennas in the area. 256 antennas cost 1.9 ms a frame on the stand, 73 us each.
    ///
    /// The call now runs once every <see cref="SeenPeriod"/> of those updates (every 30 frames, half a
    /// second) on a grid replicated to some player, and once every <see cref="IdlePeriod"/> (every 100
    /// frames) on a grid replicated to none (<c>PlayerPresenceTier</c> is not Normal); antennas are spread
    /// over the frames. What it feeds - relay reach for terminals, remote control and IGC - notices a new
    /// antenna in range that much later; a character already does its own half of this every 40 frames
    /// (CharacterUpdate10). Switching the antenna on or off, or changing its broadcasting, still updates
    /// at once: that goes through another call site, which is not rewritten.
    /// </summary>
    [PatchShim]
    public static class AntennaUpdate10
    {
        /// <summary>On a grid some player sees, one in this many ten-frame updates does the work.</summary>
        public const int SeenPeriod = 3;

        /// <summary>On a grid no player sees, one in this many ten-frame updates does the work.</summary>
        public const int IdlePeriod = 10;

        private static readonly MethodInfo BroadcastersUpdate =
            typeof(MyRadioReceiver).GetMethod(nameof(MyRadioReceiver.UpdateBroadcastersInRange),
                BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("AntennaUpdate10", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            if (BroadcastersUpdate == null) throw new MissingMethodException("MyRadioReceiver.UpdateBroadcastersInRange");

            var update10 = typeof(MyRadioAntenna).GetMethod(nameof(MyRadioAntenna.UpdateAfterSimulation10),
                BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (update10 == null || update10.DeclaringType != typeof(MyRadioAntenna))
                throw new MissingMethodException("MyRadioAntenna.UpdateAfterSimulation10");

            ctx.GetPattern(update10).Transpilers.Add(
                typeof(AntennaUpdate10).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>Replaces the one radio call with the throttled version; the rest stays as it is.</summary>
        internal static IEnumerable<MsilInstruction> Transpiler(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            var replaced = 0;

            for (var i = 0; i < list.Count; i++)
            {
                if (!Calls(list[i], BroadcastersUpdate)) continue;
                list[i] = new MsilInstruction(OpCodes.Call).InlineValue((MethodBase)
                    typeof(AntennaUpdate10).GetMethod(nameof(MaybeUpdateBroadcasters), BindingFlags.Static | BindingFlags.Public));
                replaced++;
            }

            if (replaced != 1)
                throw new InvalidOperationException("AntennaUpdate10: expected one radio call in UpdateAfterSimulation10, found " + replaced);
            return list;
        }

        /// <summary>
        /// True when the instruction calls that method, also when the call is emitted against the base
        /// class that declares it.
        /// </summary>
        private static bool Calls(MsilInstruction instruction, MethodInfo method)
        {
            if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt) return false;
            if (!(instruction.Operand is MsilOperandInline<MethodBase> operand)) return false;
            if (!(operand.Value is MethodInfo called)) return false;
            if (called == method || called.GetBaseDefinition() == method.GetBaseDefinition()) return true;
            return called.Name == method.Name && called.GetParameters().Length == 0 &&
                   called.DeclaringType != null && method.DeclaringType != null &&
                   (called.DeclaringType.IsAssignableFrom(method.DeclaringType) ||
                    method.DeclaringType.IsAssignableFrom(called.DeclaringType));
        }

        public static void MaybeUpdateBroadcasters(MyRadioReceiver receiver)
        {
            if (receiver != null && Due(receiver)) receiver.UpdateBroadcastersInRange();
        }

        /// <summary>
        /// One in <see cref="SeenPeriod"/> ten-frame updates on a grid some player sees, one in
        /// <see cref="IdlePeriod"/> on the others; a different one for every antenna, so a fleet does not
        /// land on the same frame.
        /// </summary>
        internal static bool Due(MyRadioReceiver receiver)
        {
            var block = receiver.Entity as MyCubeBlock;
            var grid = block?.CubeGrid;
            if (grid == null || MySandboxGame.Static == null) return true;
            var period = grid.PlayerPresenceTier == MyUpdateTiersPlayerPresence.Normal ? SeenPeriod : IdlePeriod;
            var tick = MySandboxGame.Static.SimulationFrameCounter / 10;
            return (tick + (ulong)(uint)RuntimeHelpers.GetHashCode(receiver)) % (ulong)period == 0;
        }
    }
}
