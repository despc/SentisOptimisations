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
    public static class ConveyorPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var MethodOnBlockAdded = typeof(MyCubeGridSystems).GetMethod
                (nameof(MyCubeGridSystems.OnBlockAdded), BindingFlags.Instance | BindingFlags.Public);


            ctx.GetPattern(MethodOnBlockAdded).Prefixes.Add(
                typeof(ConveyorPatch).GetMethod(nameof(MethodOnBlockAddedPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static void MethodOnBlockAddedPatched(MyCubeGridSystems __instance, MySlimBlock block)
        {
            if (VoxelsPatch.Protectors == null)
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
                                VoxelsPatch.Protectors = new HashSet<IMyUpgradeModule>();
                                return;
                            }

                            VoxelsPatch.Protectors = (HashSet<IMyUpgradeModule>)fieldProtectors.GetValue(null);
                        }
                    }
                }
            }
        }
    }
}