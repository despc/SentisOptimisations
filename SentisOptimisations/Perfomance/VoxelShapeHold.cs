using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Sandbox;
using Sandbox.Engine.Voxels;
using Torch.Managers.PatchManager;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Collision meshes of voxel cells that Havok had to ask for in the middle of a query are kept for ten minutes.
    ///
    /// A voxel body drops its cells' meshes when nothing that moves is near it (MyVoxelPhysicsBody.CheckAndDiscardShapes,
    /// some 3 s after the last hit), and makes them ahead only for grids and floating objects that come near. A ray that
    /// a block of a base casts into the ground meets cells with no mesh: Havok asks for them at once
    /// (RequestShapeBatchBlockingInternal) and the ray waits while they are made - and so does every other ray of that
    /// physics step. The meshes are dropped again seconds later, and the block's next ray makes them again.
    ///
    /// Measured on a world with no player (06.10.2026): one wind turbine's ray down to the ground, once in 45 s on a
    /// grid no player sees (WindTurbineRays), had 5-6 cells made each time, 5-8 ms; the four other rays of the step
    /// took the same 5-8 ms each, waiting. A physics step of 7-8 ms every 45 s for one turbine.
    ///
    /// A body Havok asked a mesh of is not let drop its meshes for <see cref="HoldFrames"/>: a block that keeps casting
    /// there has them made once in ten minutes instead of every time. What is held is the cells the queries touched, a
    /// few meshes a body; a body nothing asks of drops them as before.
    /// </summary>
    [PatchShim]
    public static class VoxelShapeHold
    {
        /// <summary>Frames the meshes of a body are kept for after Havok asked for one of them.</summary>
        public const int HoldFrames = 10 * 60 * 60;

        private sealed class Asked
        {
            public long Frame;
        }

        private static readonly ConditionalWeakTable<MyVoxelPhysicsBody, Asked> ByBody = new ConditionalWeakTable<MyVoxelPhysicsBody, Asked>();
        private static int _anyAsked;

        // Read by SentisTests.
        public static long Held;

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("VoxelShapeHold", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var request = typeof(MyVoxelPhysicsBody).GetMethod("RequestShapeBatchBlockingInternal", BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new MissingMethodException("MyVoxelPhysicsBody.RequestShapeBatchBlockingInternal");
            var discard = typeof(MyVoxelPhysicsBody).GetMethod("CheckAndDiscardShapes", BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new MissingMethodException("MyVoxelPhysicsBody.CheckAndDiscardShapes");
            ctx.GetPattern(request).Prefixes.Add(typeof(VoxelShapeHold).GetMethod(nameof(RequestPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            ctx.GetPattern(discard).Prefixes.Add(typeof(VoxelShapeHold).GetMethod(nameof(DiscardPrefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        // Havok calls it from a query or its step, on the game thread or a worker
        private static void RequestPrefix(MyVoxelPhysicsBody __instance)
        {
            var game = MySandboxGame.Static;
            if (game == null) return;
            Volatile.Write(ref ByBody.GetOrCreateValue(__instance).Frame, (long)game.SimulationFrameCounter);
            Volatile.Write(ref _anyAsked, 1);
        }

        // every ten frames of every voxel body with physics: nothing looked up until some body was asked
        private static bool DiscardPrefix(MyVoxelPhysicsBody __instance)
        {
            if (Volatile.Read(ref _anyAsked) == 0) return true;
            var game = MySandboxGame.Static;
            if (game == null || !ByBody.TryGetValue(__instance, out var asked)) return true;
            if (!Holds(Volatile.Read(ref asked.Frame), (long)game.SimulationFrameCounter)) return true;
            Interlocked.Increment(ref Held);
            return false;
        }

        /// <summary>Whether a body asked for a mesh at <paramref name="askedFrame"/> still keeps its meshes at <paramref name="frame"/>.</summary>
        public static bool Holds(long askedFrame, long frame) => frame >= askedFrame && frame - askedFrame < HoldFrames;
    }
}
