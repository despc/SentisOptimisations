using System.Collections.Generic;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class FindVoxelProtectorsPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var MethodOnBlockAdded = typeof(MyCubeGridSystems).GetMethod
                (nameof(MyCubeGridSystems.OnBlockAdded), BindingFlags.Instance | BindingFlags.Public);


            ctx.GetPattern(MethodOnBlockAdded).Prefixes.Add(
                typeof(FindVoxelProtectorsPatch).GetMethod(nameof(MethodOnBlockAddedPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static void MethodOnBlockAddedPatched(MyCubeGridSystems __instance, MySlimBlock block)
        {
            if (VoxelProtectorPatch.Protectors == null)
            {
                if (block.FatBlock is MyUpgradeModule)
                {
                    foreach (var myEntityComponent in block.FatBlock.Components)
                    {
                        if (myEntityComponent.GetType().Name.Equals("NanoBotSuppressor"))
                        {
                            var fieldProtectors = myEntityComponent.GetType().GetField("Protectors");
                            if (fieldProtectors == null)
                            {
                                Log.Error("No voxel protector support");
                                VoxelProtectorPatch.Protectors = new HashSet<IMyUpgradeModule>();
                                return;
                            }

                            VoxelProtectorPatch.Protectors = (HashSet<IMyUpgradeModule>)fieldProtectors.GetValue(null);
                        }
                    }
                }
            }
        }
    }
}