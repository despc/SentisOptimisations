using System;
using System.Reflection;
using NLog;
using Sandbox.Game.World.Generator;
using Torch.Managers.PatchManager;
using VRage.Game;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class VoxelPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var assembly = typeof(MyContractGenerator).Assembly;
            var type = assembly.GetType("Sandbox.Game.World.Generator.MyCompositeShapes");

            var IsAcceptedAsteroidMaterial = type.GetMethod("IsAcceptedAsteroidMaterial",
                BindingFlags.Static | BindingFlags.NonPublic);


            
            ctx.GetPattern(IsAcceptedAsteroidMaterial).Prefixes.Add(
                typeof(VoxelPatch).GetMethod(nameof(IsAcceptedAsteroidMaterialPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool IsAcceptedAsteroidMaterialPatched(MyVoxelMaterialDefinition material, int version, ref bool __result)
        {
            try
            {
                var acceptedAsteroidMaterials = SentisOptimisationsPlugin.Config.AcceptedAsteroidMaterials;
                var materials = acceptedAsteroidMaterials.Split(';');
                if (materials.Contains(material.MinedOre))
                {
                    __result = true;
                }
                return false;
            }
            catch (Exception e)
            {
                Log.Error("Exception in time  IsAcceptedAsteroidMaterial patch", e);
                return true;
            }
        }
    }
}