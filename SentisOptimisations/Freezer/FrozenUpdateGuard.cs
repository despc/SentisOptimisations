using System;
using System.Reflection;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using VRage.ModAPI;

namespace SentisOptimisationsPlugin.Freezer
{
    /// <summary>
    /// A frozen entity stays out of the update lists until it is thawed.
    ///
    /// The freezer takes a frozen grid and everything in it off the updates and marks them (entity flag 4, unused by
    /// the game). But a block that changes its own update needs (<c>NeedsUpdate</c>, the parallel update flags) is put
    /// back into the update lists by the game there and then - frozen or not. A frozen ship's connectors kept running
    /// their update every 10 frames that way (frozen_save_perf, 28.09.2026: the connector timers of a frozen grid
    /// ticking on between the prepared save and the real one). A marked entity is not taken in; the thaw clears the
    /// mark first and registers it with the needs it has then.
    /// </summary>
    [PatchShim]
    public static class FrozenUpdateGuard
    {
        public const EntityFlags FrozenMark = (EntityFlags)4;

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("FrozenUpdateGuard", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var type = typeof(MyParallelEntityUpdateOrchestrator);
            var prefix = typeof(FrozenUpdateGuard).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic);
            foreach (var name in new[] { "AddEntity", "EntityFlagsChanged" })
            {
                var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(MyEntity) }, null)
                             ?? throw new MissingMethodException(type.Name, name);
                ctx.GetPattern(method).Prefixes.Add(prefix);
            }
        }

        /// <summary>False (not taken in) for an entity the freezer marked.</summary>
        public static bool Admit(EntityFlags flags) => (flags & FrozenMark) == 0;

        private static bool Prefix(MyEntity entity) => entity == null || Admit(entity.Flags);
    }
}

