using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Sandbox;
using Sandbox.Game.Weapons;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using VRage.Game;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Ship drill results carry only the materials that were actually cut.
    ///
    /// When a drill cut-out finishes, vanilla builds for every drill a new dictionary with an
    /// entry for every voxel material of the game (~60 here), most of them 0, and the drill then
    /// walks all of them: for each material with ore it creates an ore object builder, bumps the
    /// session's mined-ore statistics and calls AddItems, which returns at once for 0. With 32
    /// ships of 36 drills that was ~75 thousand results and ~4.7 million AddItems calls every two
    /// minutes and half a gigabyte of garbage.
    ///
    /// The dictionary is now one reused instance filled with the non-zero materials only (the
    /// results are consumed synchronously inside the same call). What is lost for a zero entry:
    /// an ore object builder that is thrown away, a 0 added to the mined-ore statistics (which
    /// only creates the key), and a ShipDrillCollected visual-scripting event reporting 0.
    /// </summary>
    [PatchShim]
    public static class DrillCutoutResults
    {
        private static readonly Dictionary<MyVoxelMaterialDefinition, int> Reused = new Dictionary<MyVoxelMaterialDefinition, int>();

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("DrillCutoutResults", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var finish = typeof(MyShipDrill).Assembly.GetType("Sandbox.Game.GameSystems.MyShipMiningSystem", true)
                .GetNestedType("ClusterCutOut", BindingFlags.Public | BindingFlags.NonPublic)?
                .GetMethod("Finish", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (finish == null) throw new MissingMethodException("DrillCutoutResults: ClusterCutOut.Finish not found");
            ctx.GetPattern(finish).Transpilers.Add(typeof(DrillCutoutResults).GetMethod(nameof(FinishTranspiler),
                BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>
        /// <c>new Dictionary&lt;MyVoxelMaterialDefinition, int&gt;()</c> becomes <see cref="Rent"/>,
        /// its indexer setter becomes <see cref="SetNonZero"/>.
        /// </summary>
        private static IEnumerable<MsilInstruction> FinishTranspiler(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            var type = typeof(Dictionary<MyVoxelMaterialDefinition, int>);
            var ctor = type.GetConstructor(Type.EmptyTypes);
            var setter = type.GetProperty("Item").SetMethod;
            var rent = typeof(DrillCutoutResults).GetMethod(nameof(Rent), BindingFlags.Static | BindingFlags.Public);
            var set = typeof(DrillCutoutResults).GetMethod(nameof(SetNonZero), BindingFlags.Static | BindingFlags.Public);
            int ctors = 0, sets = 0;
            for (var i = 0; i < list.Count; i++)
            {
                if (!(list[i].Operand is MsilOperandInline<MethodBase> operand)) continue;
                MethodBase replacement;
                if (list[i].OpCode == OpCodes.Newobj && operand.Value == ctor) { replacement = rent; ctors++; }
                else if (list[i].OpCode == OpCodes.Callvirt && operand.Value == setter) { replacement = set; sets++; }
                else continue;
                var call = new MsilInstruction(OpCodes.Call).InlineValue(replacement);
                foreach (var label in list[i].Labels) call.Labels.Add(label);
                list[i] = call;
            }
            if (ctors != 1 || sets != 1)
                throw new InvalidOperationException("DrillCutoutResults: unexpected ClusterCutOut.Finish IL (" + ctors + " dictionaries, " + sets + " setters)");
            return list;
        }

        public static Dictionary<MyVoxelMaterialDefinition, int> Rent()
        {
            // Finish runs from the parallel-task callbacks on the game thread; anywhere else, vanilla.
            if (MySandboxGame.Static?.UpdateThread != Thread.CurrentThread)
                return new Dictionary<MyVoxelMaterialDefinition, int>();
            Reused.Clear();
            return Reused;
        }

        public static void SetNonZero(Dictionary<MyVoxelMaterialDefinition, int> materials, MyVoxelMaterialDefinition material, int amount)
        {
            if (amount != 0) materials[material] = amount;
        }
    }
}
