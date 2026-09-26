using System;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The check for floating objects stuck in the ground every fourth frame, not every frame.
    ///
    /// <c>MyFloatingObjects.CheckObjectInVoxel</c> takes one floating object a frame, in turn, and reads the voxel
    /// storage at each of the 8 corners of its box, one read each through the storage's octree: 0.15 ms a frame on
    /// the stand with the bots mining (dotTrace: 5.3 s in 10 minutes, nearly all in <c>MyOctreeStorage.ReadRange</c>),
    /// whether there are two floating objects or two thousand. An object found inside the ground on 6 checks running
    /// is removed. Here the check runs on every fourth frame: the same objects removed, a few dozen frames later.
    /// </summary>
    [PatchShim]
    public static class FloatingInVoxelCheck
    {
        /// <summary>The check runs on one frame in this many.</summary>
        public const int Every = 4;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("FloatingInVoxelCheck", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var method = typeof(MyFloatingObjects).GetMethod("CheckObjectInVoxel", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                         ?? throw new MissingMethodException("MyFloatingObjects.CheckObjectInVoxel");
            ctx.GetPattern(method).Prefixes.Add(typeof(FloatingInVoxelCheck).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>Whether the check runs on this frame.</summary>
        public static bool RunsOn(int frame) => frame % Every == 0;

        private static bool Prefix() => RunsOn(MySession.Static?.GameplayFrameCounter ?? 0);
    }
}
