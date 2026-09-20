using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Sandbox;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// The two expensive things a character does ten times a second are done less often.
    ///
    /// <c>MyCharacter.UpdateBeforeSimulation10</c> is the costliest of a character's own updates -
    /// 37 microseconds per character on the stand, against 1 microsecond for its every-frame update -
    /// and all of it is these two calls:
    /// <list type="bullet">
    /// <item><c>SuitRechargeDistributor.UpdateBeforeSimulation()</c> - the resource distributor of the
    /// suit. It only recomputes when a sink or a source changed, so calling it less often merely
    /// delays noticing that;</item>
    /// <item><c>RadioReceiver.UpdateBroadcastersInRange()</c> - a scan of every antenna that can
    /// reach the character, which raises found/lost events and refreshes the antenna's own list of
    /// receivers. The list stays as it was between calls; a new antenna is simply noticed later.</item>
    /// </list>
    ///
    /// Both now run once every <see cref="Period"/> of those updates, characters spread over them so
    /// they do not all land on the same frame. The delay is under a second, and vanilla itself only
    /// does this ten times a second. Everything else in the method - and every other character update
    /// - is untouched, as is the same radio update on antennas and grids, because only this call site
    /// is rewritten.
    /// </summary>
    [PatchShim]
    public static class CharacterUpdate10
    {
        /// <summary>One in this many ten-frame updates does the work; the rest skip it.</summary>
        public const int Period = 4;

        private static readonly MethodInfo DistributorUpdate =
            typeof(MyResourceDistributorComponent).GetMethod(nameof(MyResourceDistributorComponent.UpdateBeforeSimulation),
                BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
        private static readonly MethodInfo BroadcastersUpdate =
            typeof(MyRadioReceiver).GetMethod(nameof(MyRadioReceiver.UpdateBroadcastersInRange),
                BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("CharacterUpdate10", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            if (DistributorUpdate == null) throw new MissingMethodException("MyResourceDistributorComponent.UpdateBeforeSimulation");
            if (BroadcastersUpdate == null) throw new MissingMethodException("MyRadioReceiver.UpdateBroadcastersInRange");

            var update10 = typeof(MyCharacter).GetMethod(nameof(MyCharacter.UpdateBeforeSimulation10),
                BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (update10 == null) throw new MissingMethodException("MyCharacter.UpdateBeforeSimulation10");

            ctx.GetPattern(update10).Transpilers.Add(
                typeof(CharacterUpdate10).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>
        /// Replaces the two calls with the throttled versions. Anything else the method does stays
        /// exactly as it is.
        /// </summary>
        internal static IEnumerable<MsilInstruction> Transpiler(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            var self = typeof(CharacterUpdate10);
            var replaced = 0;

            for (var i = 0; i < list.Count; i++)
            {
                if (Calls(list[i], DistributorUpdate))
                {
                    list[i] = Call(self.GetMethod(nameof(MaybeUpdateSuitPower), BindingFlags.Static | BindingFlags.Public));
                    replaced++;
                }
                else if (Calls(list[i], BroadcastersUpdate))
                {
                    list[i] = Call(self.GetMethod(nameof(MaybeUpdateBroadcasters), BindingFlags.Static | BindingFlags.Public));
                    replaced++;
                }
            }

            if (replaced != 2)
                throw new InvalidOperationException("CharacterUpdate10: expected both calls in UpdateBeforeSimulation10, found " + replaced);
            return list;
        }

        /// <summary>
        /// True when the instruction calls that method. The comparison goes through the original
        /// declaration: the call to the radio receiver is emitted against the base class, so
        /// comparing the override itself never matches.
        /// </summary>
        private static bool Calls(MsilInstruction instruction, MethodInfo method)
        {
            if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt) return false;
            if (!(instruction.Operand is MsilOperandInline<MethodBase> operand)) return false;
            if (!(operand.Value is MethodInfo called)) return false;
            if (called == method || called.GetBaseDefinition() == method.GetBaseDefinition()) return true;
            // The call can be emitted against a base class that declares the same method.
            return called.Name == method.Name && called.GetParameters().Length == 0 &&
                   called.DeclaringType != null && method.DeclaringType != null &&
                   (called.DeclaringType.IsAssignableFrom(method.DeclaringType) ||
                    method.DeclaringType.IsAssignableFrom(called.DeclaringType));
        }

        private static MsilInstruction Call(MethodInfo method) => new MsilInstruction(OpCodes.Call).InlineValue((MethodBase)method);

        public static void MaybeUpdateSuitPower(MyResourceDistributorComponent distributor)
        {
            if (distributor != null && Due(distributor)) distributor.UpdateBeforeSimulation();
        }

        public static void MaybeUpdateBroadcasters(MyRadioReceiver receiver)
        {
            if (receiver != null && Due(receiver)) receiver.UpdateBroadcastersInRange();
        }

        /// <summary>
        /// One in <see cref="Period"/> of the ten-frame updates, and a different one for every
        /// character, so the work of a crowd is spread instead of landing in one frame.
        /// </summary>
        private static bool Due(object owner)
        {
            if (MySandboxGame.Static == null) return true;
            var tick = MySandboxGame.Static.SimulationFrameCounter / 10;
            return (tick + (ulong)(uint)RuntimeHelpers.GetHashCode(owner)) % Period == 0;
        }
    }
}
