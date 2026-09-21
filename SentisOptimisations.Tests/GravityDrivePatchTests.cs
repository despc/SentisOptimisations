using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using SentisGameplayImprovements;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using VRage.Game.Components;
using Xunit;

namespace SentisOptimisations.Tests;

/// <summary>
/// The gravity drive fix runs against the game's own IL: the generator's push of artificial mass
/// goes through PushMass, the push of loose objects stays the game's.
/// </summary>
public class GravityDrivePatchTests
{
    private static readonly MethodInfo Update = typeof(MyGravityGeneratorBase).GetMethod(nameof(MyGravityGeneratorBase.UpdateBeforeSimulation),
        BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);

    private static readonly MethodInfo PushMass = typeof(GravityDrivePatch).GetMethod(nameof(GravityDrivePatch.PushMass));

    private static List<MsilInstruction> Transpile()
    {
        var input = PatchUtilities.ReadInstructions(Update).ToList();
        var transpiler = typeof(GravityDrivePatch).GetMethod("Transpiler", BindingFlags.Static | BindingFlags.NonPublic);
        return ((IEnumerable<MsilInstruction>)transpiler.Invoke(null, new object[] { input })).ToList();
    }

    private static bool Calls(MsilInstruction instruction, MethodBase method) =>
        instruction.Operand is MsilOperandInline<MethodBase> operand && operand.Value == method;

    [Fact]
    public void The_push_of_artificial_mass_goes_through_PushMass_with_the_generator()
    {
        var output = Transpile();
        var at = output.FindIndex(i => Calls(i, PushMass));
        Assert.True(at > 0, "PushMass is not called");
        Assert.Single(output, i => Calls(i, PushMass));
        Assert.Equal(OpCodes.Ldarg_0, output[at - 1].OpCode);
    }

    [Fact]
    public void The_push_of_loose_objects_stays_the_games()
    {
        var addForce = typeof(MyPhysicsComponentBase).GetMethod(nameof(MyPhysicsComponentBase.AddForce));
        Assert.Single(Transpile(), i => Calls(i, addForce));
    }

    [Fact]
    public void PushMass_takes_what_AddForce_takes_and_then_the_generator()
    {
        var addForce = typeof(MyPhysicsComponentBase).GetMethod(nameof(MyPhysicsComponentBase.AddForce));
        var expected = new[] { typeof(MyPhysicsComponentBase) }
            .Concat(addForce.GetParameters().Select(p => p.ParameterType))
            .Concat(new[] { typeof(MyGravityGeneratorBase) })
            .ToArray();
        Assert.Equal(expected, PushMass.GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Equal(typeof(void), PushMass.ReturnType);
    }
}
