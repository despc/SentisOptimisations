using System.Reflection;
using Sandbox;
using Torch.Managers.PatchManager;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Refreshes the planetary encounters' player presence every tenth frame instead of every frame.
    ///
    /// MyPlanetaryEncountersGenerator.UpdateBeforeSimulation calls SpawnAreaChecker.UpdatePlayerPresence
    /// for every player every frame: a sphere query over the tree of static objects and a pass over
    /// everything found, only to reset the absence counter of nearby encounter installations. With 64
    /// players near 64 static bases that was 0.9 s of every 55 on the replication test, a quarter of a
    /// millisecond a frame. The counter grows by one a frame and an installation is despawned once it
    /// passes 60, so a refresh every <see cref="PeriodFrames"/> frames keeps it far below that; an
    /// installation waiting to spawn next to a player spawns up to that many frames later. The players
    /// are spread over the frames by the order they are checked in, so the checks do not line up.
    /// </summary>
    [PatchShim]
    public static class EncounterPresenceThrottle
    {
        /// <summary>Frames between presence refreshes of one player; the despawn limit is 60.</summary>
        public const int PeriodFrames = 10;

        private static ulong _frame = ulong.MaxValue;
        private static uint _call;

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("EncounterPresenceThrottle", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var generator = typeof(SpaceEngineers.Game.Entities.Blocks.MyShipWelder).Assembly
                .GetType("SpaceEngineers.Game.SessionComponents.MyPlanetaryEncountersGenerator");
            var checker = generator?.GetNestedType("SpawnAreaChecker", BindingFlags.NonPublic | BindingFlags.Public);
            var method = checker?.GetMethod("UpdatePlayerPresence", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null) return;
            ctx.GetPattern(method).Prefixes.Add(typeof(EncounterPresenceThrottle).GetMethod(nameof(UpdatePlayerPresencePrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool UpdatePlayerPresencePrefix()
        {
            var frame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            if (frame != _frame)
            {
                _frame = frame;
                _call = 0;
            }
            // The n-th check of a frame belongs to the same player every frame (the players are
            // walked in the same order), so each one runs every PeriodFrames frames, offset by n.
            return (frame + _call++) % PeriodFrames == 0;
        }
    }
}
