using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRage.Game;

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

        public class CashedEntry
        {
            private long _startBlockEntityId;
            private long _endBlockEntityId;
            private MyDefinitionId? _itemId;

            public long StartBlockEntityId
            {
                get => _startBlockEntityId;
                set => _startBlockEntityId = value;
            }

            public long EndBlockEntityId
            {
                get => _endBlockEntityId;
                set => _endBlockEntityId = value;
            }

            public MyDefinitionId? ItemId
            {
                get => _itemId;
                set => _itemId = value;
            }

            public CashedEntry(long startBlockEntityId, long endBlockEntityId, MyDefinitionId? itemId)
            {
                _startBlockEntityId = startBlockEntityId;
                _endBlockEntityId = endBlockEntityId;
                _itemId = itemId;
            }

            public override bool Equals(object obj)
            {
                if (obj == null)
                {
                    return false;
                }

                if (obj is CashedEntry)
                {
                    if (((CashedEntry)obj).ItemId == null)
                    {
                        if (this.ItemId != null)
                        {
                            return false;
                        }

                        return ((CashedEntry)obj).StartBlockEntityId == this.StartBlockEntityId &&
                               ((CashedEntry)obj).EndBlockEntityId == this.EndBlockEntityId;
                    }

                    return ((CashedEntry)obj).StartBlockEntityId == this.StartBlockEntityId &&
                           ((CashedEntry)obj).EndBlockEntityId == this.EndBlockEntityId &&
                           ((CashedEntry)obj).ItemId.Equals(this.ItemId);
                }

                return false;
            }

            public override int GetHashCode()
            {
                int hashcode = ItemId != null ? ItemId.GetHashCode() : 0;
                hashcode = (int)(hashcode + (StartBlockEntityId & 0xFFFFFFFF) + (EndBlockEntityId & 0xFFFFFFFF));
                return hashcode;
            }
        }
    }
}