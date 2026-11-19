using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Optimizer.Optimizations;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using VRageMath;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class WelderTargetsTests
    {
        [Fact]
        public void Sphere_query_keeps_both_vanilla_walks_and_their_branch_target()
        {
            var original = PatchUtilities.ReadInstructions(typeof(MyCubeGrid).GetMethod(nameof(MyCubeGrid.GetBlocksInsideSphereInternal))).ToList();
            var count = typeof(ConcurrentDictionary<Vector3I, MyCube>).GetProperty("Count").GetGetMethod();
            var i = original.FindIndex(op => op.Operand is MsilOperandInline<MethodBase> method && method.Value == count);
            Assert.True(i > 0);
            var transpiler = typeof(WelderTargets).GetMethod("SphereCountTranspiler", BindingFlags.Static | BindingFlags.NonPublic);
            var rewritten = ((IEnumerable<MsilInstruction>)transpiler.Invoke(null, new object[] { original })).ToList();

            Assert.Equal(original.Count + 1, rewritten.Count);
            Assert.Equal(OpCodes.Ldarg_0, rewritten[i].OpCode);
            Assert.Equal(original[i].Labels, rewritten[i].Labels);
            Assert.Equal(typeof(WelderTargets).GetMethod(nameof(WelderTargets.WalkSphereCells)),
                ((MsilOperandInline<MethodBase>)rewritten[i + 1].Operand).Value);
            Assert.Equal(OpCodes.Brfalse, rewritten[i + 2].OpCode);
            Assert.Same(((MsilOperandBrTarget)original[i + 1].Operand).Target, ((MsilOperandBrTarget)rewritten[i + 2].Operand).Target);
            Assert.Equal(original.Take(i), rewritten.Take(i));
            Assert.Equal(original.Skip(i + 2), rewritten.Skip(i + 3));
        }

        [Fact]
        public void Outside_our_welder_scan_the_comparison_is_strict_and_uses_the_cell_count()
        {
            var cubes = new ConcurrentDictionary<Vector3I, MyCube>();
            cubes[Vector3I.Zero] = null;
            cubes[Vector3I.One] = null;
            Assert.True(WelderTargets.WalkSphereCells(1, cubes, null));
            Assert.False(WelderTargets.WalkSphereCells(2, cubes, null));
            Assert.False(WelderTargets.WalkSphereCells(3, cubes, null));
        }
    }
}
