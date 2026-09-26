using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Sandbox.Game.Components;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using VRage.Game.Components;
using VRage.Utils;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// No reading of the ground's material under a character's feet for the sound of its steps on a dedicated server.
    ///
    /// A walking character's sound component picks the sound of its steps by the material it stands on
    /// (<c>MyCharacterSoundComponent.FindSupportingMaterial</c>), in the parallel update, every frame: on a planet a read
    /// of the voxel storage under its lock. A dedicated server plays no sound; the reads only queue up with the
    /// drills and the other readers of the planet (a frame of 72 ms waiting in that update on the stand).
    ///
    /// Here, on a dedicated server, that one read gives "no material" - what follows in the method is the game's (the
    /// grid's material, else rock), and so is what came before it (the support the character stands on, which the
    /// character's jumping uses).
    /// </summary>
    [PatchShim]
    public static class CharacterStepMaterial
    {
        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("CharacterStepMaterial", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            if (!Sandbox.Engine.Platform.Game.IsDedicated) return;
            var find = typeof(MyCharacterSoundComponent).GetMethod("FindSupportingMaterial", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                       ?? throw new MissingMethodException("MyCharacterSoundComponent.FindSupportingMaterial");
            ctx.GetPattern(find).Transpilers.Add(typeof(CharacterStepMaterial).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static IEnumerable<MsilInstruction> Transpiler(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            var found = 0;
            for (var i = 0; i < list.Count; i++)
            {
                if (!(list[i].Operand is MsilOperandInline<MethodBase> operand) || operand.Value.Name != "GetMaterialAt" ||
                    !typeof(MyPhysicsComponentBase).IsAssignableFrom(operand.Value.DeclaringType)) continue;
                var parameters = operand.Value.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(Vector3D)) continue;
                var call = new MsilInstruction(OpCodes.Call).InlineValue(typeof(CharacterStepMaterial).GetMethod(nameof(NoMaterial), BindingFlags.Static | BindingFlags.NonPublic));
                foreach (var label in list[i].Labels) call.Labels.Add(label);
                list[i] = call;
                found++;
            }
            if (found != 1)
            {
                SentisOptimisationsPlugin.Log.Error($"CharacterStepMaterial: {found} GetMaterialAt in FindSupportingMaterial, not 1; left as it is");
                return instructions;
            }
            return list;
        }

        private static MyStringHash NoMaterial(MyPhysicsComponentBase physics, Vector3D at) => MyStringHash.NullOrEmpty;
    }
}
