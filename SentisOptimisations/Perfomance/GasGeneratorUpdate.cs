using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Multiplayer;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// What an O2/H2 generator recomputes every frame, done every tenth frame.
    ///
    /// A producing generator keeps MyGasGenerator.UpdateAfterSimulation on every frame, and two of
    /// the things it does there are the bulk of its cost: SetRemainingCapacities, which writes the
    /// gas still left in the ice into the source component, and ResourceSink.Update, which asks the
    /// generator for its power draw again (ComputeRequiredPower -> GetIsProducing -> a lookup per
    /// produced gas). Measured on 400 producing generators: 0.82 ms of every frame, of which
    /// 0.25 ms in SetRemainingCapacities and most of the rest inside the sink update.
    ///
    /// Neither answer changes quickly. The power draw only switches between the operational and the
    /// standby figure, and vanilla already refreshes the sink whenever the generator is enabled,
    /// stops working or changes its inventory; the remaining capacity follows the ice, which a
    /// generator burns at a few tens of kilograms a second. Both now run on every
    /// <see cref="PeriodFrames"/>th frame, generators spread over those frames by entity id, and
    /// always on the frame a generator is down to its last second of ice, so it still stops
    /// producing the moment the ice runs out.
    /// </summary>
    [PatchShim]
    public static class GasGeneratorUpdate
    {
        public const int PeriodFrames = 10;

        private static readonly MethodInfo SetRemainingCapacitiesMethod =
            typeof(MyGasGenerator).GetMethod("SetRemainingCapacities", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo SinkUpdateMethod = typeof(MyResourceSinkComponent).GetMethod(nameof(MyResourceSinkComponent.Update));
        private static readonly Action<MyGasGenerator> SetRemainingCapacities = SetRemainingCapacitiesMethod != null
            ? (Action<MyGasGenerator>)Delegate.CreateDelegate(typeof(Action<MyGasGenerator>), SetRemainingCapacitiesMethod, false)
            : null;
        private static readonly Func<MyGasGenerator, float> GetIceAmount = BuildIceGetter();

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("GasGeneratorUpdate", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            if (SetRemainingCapacities == null || GetIceAmount == null || SinkUpdateMethod == null) return;
            var update = typeof(MyGasGenerator).GetMethod(nameof(MyGasGenerator.UpdateAfterSimulation),
                BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            ctx.GetPattern(update).Transpilers.Add(typeof(GasGeneratorUpdate).GetMethod(nameof(UpdateAfterSimulationTranspiler),
                BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static Func<MyGasGenerator, float> BuildIceGetter()
        {
            var field = typeof(MyGasGenerator).GetField("m_iceAmount", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null || field.FieldType != typeof(float)) return null;
            var method = new DynamicMethod("GetIceAmount", typeof(float), new[] { typeof(MyGasGenerator) }, typeof(MyGasGenerator), true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<MyGasGenerator, float>)method.CreateDelegate(typeof(Func<MyGasGenerator, float>));
        }

        /// <summary>
        /// Replaces <c>SetRemainingCapacities()</c> with <c>MaybeSetRemainingCapacities(this)</c> and
        /// <c>base.ResourceSink.Update()</c> with <c>MaybeUpdateSink(this)</c>.
        /// </summary>
        internal static IEnumerable<MsilInstruction> UpdateAfterSimulationTranspiler(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            var self = typeof(GasGeneratorUpdate);

            var capacities = list.FindIndex(i => Calls(i, SetRemainingCapacitiesMethod));
            if (capacities < 0) throw new InvalidOperationException("GasGeneratorUpdate: SetRemainingCapacities not found");
            list[capacities] = Call(self.GetMethod(nameof(MaybeSetRemainingCapacities), BindingFlags.Static | BindingFlags.Public));

            // ldarg.0; call get_ResourceSink; callvirt Update  ->  ldarg.0; call MaybeUpdateSink
            var sink = list.FindIndex(i => Calls(i, SinkUpdateMethod));
            if (sink < 2 || list[sink - 2].OpCode != OpCodes.Ldarg_0 || list[sink - 1].Labels.Count > 0 || list[sink].Labels.Count > 0)
                throw new InvalidOperationException("GasGeneratorUpdate: unexpected MyGasGenerator.UpdateAfterSimulation IL");
            list[sink] = Call(self.GetMethod(nameof(MaybeUpdateSink), BindingFlags.Static | BindingFlags.Public));
            list.RemoveAt(sink - 1);
            return list;
        }

        private static bool Calls(MsilInstruction instruction, MethodInfo method) =>
            (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) &&
            instruction.Operand is MsilOperandInline<MethodBase> operand && operand.Value == method;

        private static MsilInstruction Call(MethodInfo method) => new MsilInstruction(OpCodes.Call).InlineValue((MethodBase)method);

        public static void MaybeSetRemainingCapacities(MyGasGenerator generator)
        {
            if (Due(generator)) SetRemainingCapacities(generator);
        }

        public static void MaybeUpdateSink(MyGasGenerator generator)
        {
            if (Due(generator)) generator.ResourceSink?.Update();
        }

        /// <summary>
        /// The generator's frame out of <see cref="PeriodFrames"/>, or any frame on which it is down
        /// to its last second of ice and about to stop producing.
        /// </summary>
        private static bool Due(MyGasGenerator generator)
        {
            if (!Sync.IsDedicated) return true;
            if ((MySandboxGame.Static.SimulationFrameCounter + (ulong)generator.EntityId) % PeriodFrames == 0) return true;
            var definition = generator.BlockDefinition as MyOxygenGeneratorDefinition;
            return definition != null && GetIceAmount(generator) <= definition.IceConsumptionPerSecond;
        }
    }
}
