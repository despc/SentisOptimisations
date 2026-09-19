using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Optimizer.Optimizations;
using Sandbox.Game.Weapons;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using VRage.Game;
using Xunit;

namespace SentisOptimisations.Tests;

/// <summary>
/// The drill transpilers run against the game's own IL: they must find exactly what they
/// replace, or the game changed and the patch has to be looked at again.
/// </summary>
public class DrillPatchesTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Type MiningSystem = typeof(MyShipDrill).Assembly.GetType("Sandbox.Game.GameSystems.MyShipMiningSystem", true);
    private static readonly Type VoxelPhysicsBody = typeof(MyShipDrill).Assembly.GetType("Sandbox.Engine.Voxels.MyVoxelPhysicsBody", true);

    private static List<MsilInstruction> Transpile(Type patch, string transpiler, MethodBase target)
    {
        var input = PatchUtilities.ReadInstructions(target).ToList();
        var method = patch.GetMethod(transpiler, Any);
        return ((IEnumerable<MsilInstruction>)method.Invoke(null, new object[] { input })).ToList();
    }

    private static bool Calls(MsilInstruction instruction, MethodBase method) =>
        instruction.Operand is MsilOperandInline<MethodBase> operand && operand.Value == method;

    [Fact]
    public void Cutout_results_use_the_reused_dictionary_and_skip_zeros()
    {
        var finish = MiningSystem.GetNestedType("ClusterCutOut", Any).GetMethod("Finish", Any);
        var output = Transpile(typeof(DrillCutoutResults), "FinishTranspiler", finish);
        var dictionary = typeof(Dictionary<MyVoxelMaterialDefinition, int>);
        Assert.DoesNotContain(output, i => i.OpCode == OpCodes.Newobj && Calls(i, dictionary.GetConstructor(Type.EmptyTypes)));
        Assert.Single(output, i => Calls(i, typeof(DrillCutoutResults).GetMethod(nameof(DrillCutoutResults.Rent))));
        Assert.Single(output, i => Calls(i, typeof(DrillCutoutResults).GetMethod(nameof(DrillCutoutResults.SetNonZero))));
    }

    [Fact]
    public void Set_non_zero_leaves_out_materials_that_were_not_cut()
    {
        var materials = new Dictionary<MyVoxelMaterialDefinition, int>();
        var stone = new MyVoxelMaterialDefinition();
        var iron = new MyVoxelMaterialDefinition();
        DrillCutoutResults.SetNonZero(materials, stone, 0);
        DrillCutoutResults.SetNonZero(materials, iron, 42);
        Assert.Equal(new[] { iron }, materials.Keys);
        Assert.Equal(42, materials[iron]);
    }

    [Fact]
    public void Voxel_invalidation_goes_through_the_cell_filter_with_body_and_lod()
    {
        var invalidate = VoxelPhysicsBody.GetMethod("InvalidateRange", Any, null,
            new[] { typeof(VRageMath.Vector3I), typeof(VRageMath.Vector3I), typeof(int) }, null);
        var output = Transpile(typeof(DrillCutPhysics), "InvalidateRangeTranspiler", invalidate);
        var at = output.FindIndex(i => Calls(i, typeof(DrillCutPhysics).GetMethod(nameof(DrillCutPhysics.InvalidateCells))));
        Assert.True(at >= 2, "InvalidateCells is not called");
        Assert.Equal(OpCodes.Ldarg_0, output[at - 2].OpCode);
        Assert.Equal(OpCodes.Ldarg_3, output[at - 1].OpCode);
        Assert.DoesNotContain(output, i => Calls(i, typeof(Havok.HkUniformGridShape).GetMethod(nameof(Havok.HkUniformGridShape.InvalidateRange))));
    }

    [Fact]
    public void Drill_move_only_marks_the_drill()
    {
        var moved = typeof(MyShipDrill).GetMethod("WorldPositionChanged", Any);
        var output = Transpile(typeof(DrillFrameUpdate), "WorldPositionChangedTranspiler", moved);
        Assert.Single(output, i => Calls(i, typeof(DrillFrameUpdate).GetMethod(nameof(DrillFrameUpdate.MarkMoved))));
        Assert.DoesNotContain(output, i => Calls(i, typeof(MyDrillBase).GetMethod(nameof(MyDrillBase.UpdatePosition))));
        // The base call stays.
        Assert.Contains(output, i => i.Operand is MsilOperandInline<MethodBase> m && m.Value.Name == "WorldPositionChanged");
    }
}

