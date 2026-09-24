using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sandbox;
using Sandbox.Game.Entities;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRage.Game.ModAPI;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// A wind turbine looks round for what blocks its wind once a second instead of all the time, and once
    /// every five seconds on a grid no player sees.
    ///
    /// A turbine measures its clearance with nine rays of 20 m, one at a time (<c>MyWindTurbine.UpdateNextRay</c>,
    /// a parallel ray whose result comes back to the game thread as an invoke). The grid's shared wind
    /// component asks every turbine of the grid for its next ray each time any of them has its ten-frame
    /// update, so with many turbines on a grid each one casts again as soon as its last ray is back: on the
    /// stand 189 turbines kept the ray queue busy, some 3 s of ray casting a minute plus the game thread
    /// waiting for it and running the results.
    ///
    /// A turbine now casts at most once every <see cref="SeenPeriod"/> frames on a grid some player sees and
    /// once every <see cref="IdlePeriod"/> on one no player sees; its nine rays go round in 9 s (45 s unseen).
    /// Clearance only changes when something is built or dug next to the turbine, which it notices that
    /// much later. The first round of rays of a turbine (a new one starts with no clearance measured) is not
    /// held back, so a turbine just built comes up to power as fast as before; after a restart the
    /// measured clearances come back with the save.
    /// </summary>
    [PatchShim]
    public static class WindTurbineRays
    {
        /// <summary>Frames between two rays of a turbine on a grid some player sees.</summary>
        public const int SeenPeriod = 60;

        /// <summary>Frames between two rays of a turbine on a grid no player sees.</summary>
        public const int IdlePeriod = 300;

        private sealed class Rays
        {
            public ulong LastFrame;
            public int Count;
        }

        private static readonly ConditionalWeakTable<MyWindTurbine, Rays> ByTurbine = new ConditionalWeakTable<MyWindTurbine, Rays>();

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("WindTurbineRays", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var nextRay = typeof(MyWindTurbine).GetMethod(nameof(MyWindTurbine.UpdateNextRay), BindingFlags.Instance | BindingFlags.Public);
            if (nextRay == null) throw new MissingMethodException("MyWindTurbine.UpdateNextRay");
            if (RayRunning == null) throw new MissingFieldException("MyWindTurbine.m_paralleRaycastRunning");
            ctx.GetPattern(nextRay).Prefixes.Add(typeof(WindTurbineRays).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static readonly FieldInfo RayRunning = typeof(MyWindTurbine).GetField("m_paralleRaycastRunning", BindingFlags.Instance | BindingFlags.NonPublic);

        private static bool Prefix(MyWindTurbine __instance)
        {
            if (MySandboxGame.Static == null) return true;
            // a ray still out: vanilla casts nothing this call anyway, and it is not counted
            if (RayRunning != null && (bool)RayRunning.GetValue(__instance)) return true;
            var grid = __instance.CubeGrid;
            var period = grid == null || grid.PlayerPresenceTier == MyUpdateTiersPlayerPresence.Normal ? SeenPeriod : IdlePeriod;
            return Due(ByTurbine.GetOrCreateValue(__instance), MySandboxGame.Static.SimulationFrameCounter, period, __instance.RayEffectivities?.Length ?? 0);
        }

        /// <summary>
        /// Whether a turbine may cast now: always for its first round of rays, then once a period. Records
        /// the cast when it may.
        /// </summary>
        public static bool Due(object state, ulong frame, int period, int raysPerRound)
        {
            var rays = (Rays)state;
            if (rays.Count >= raysPerRound && frame - rays.LastFrame < (ulong)period) return false;
            rays.LastFrame = frame;
            if (rays.Count < int.MaxValue) rays.Count++;
            return true;
        }

        public static object NewState() => new Rays();
    }
}
