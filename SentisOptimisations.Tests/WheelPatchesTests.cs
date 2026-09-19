using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Optimizer.Optimizations;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using Xunit;

namespace SentisOptimisations.Tests;

public class WheelPatchesTests
{
    private static bool Calls(MsilInstruction instruction, MethodBase method) =>
        instruction.Operand is MsilOperandInline<MethodBase> operand && operand.Value == method;

    [Fact]
    public void Suspension_sink_update_goes_through_the_throttle()
    {
        var update = typeof(MyMotorSuspension).GetMethod(nameof(MyMotorSuspension.Update), Type.EmptyTypes);
        var input = PatchUtilities.ReadInstructions(update).ToList();
        var transpiler = typeof(SuspensionPower).GetMethod("UpdateTranspiler", BindingFlags.Static | BindingFlags.NonPublic);
        var output = ((IEnumerable<MsilInstruction>)transpiler.Invoke(null, new object[] { input })).ToList();
        var at = output.FindIndex(i => Calls(i, typeof(SuspensionPower).GetMethod(nameof(SuspensionPower.UpdateSink))));
        Assert.True(at > 0, "UpdateSink is not called");
        Assert.Equal(OpCodes.Ldarg_0, output[at - 1].OpCode);
        Assert.DoesNotContain(output, i => Calls(i, typeof(MyResourceSinkComponent).GetMethod(nameof(MyResourceSinkComponent.Update), Type.EmptyTypes)));
    }
}
