using System;
using System.Reflection;
using Sandbox.Game.Entities.Character;
using Torch.Managers.PatchManager;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// A character nobody controls casts no ray for its weapon's "dynamic effective range".
    ///
    /// The server casts that ray (20 m ahead of the head, a parallel ray whose result comes back to the game
    /// thread as an invoke and a synced value) once a frame for every character in the world
    /// (<c>MyCharacter.UpdateDynamicRange</c>). It only serves a player aiming in first person - shots at
    /// something close are bent towards where the crosshair meets it - and the player's own crosshair. A
    /// body left in the world with no one controlling it (its player offline, a body left after a respawn)
    /// has no crosshair and fires nothing, yet cast the ray every frame all the same. When a player takes
    /// the body over, the ray starts again the next frame.
    /// </summary>
    [PatchShim]
    public static class IdleCharacterRange
    {
        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("IdleCharacterRange", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var update = typeof(MyCharacter).GetMethod("UpdateDynamicRange", BindingFlags.Instance | BindingFlags.NonPublic);
            if (update == null) throw new MissingMethodException("MyCharacter.UpdateDynamicRange");
            ctx.GetPattern(update).Prefixes.Add(typeof(IdleCharacterRange).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool Prefix(MyCharacter __instance) => __instance.ControllerInfo?.Controller?.Player != null;
    }
}
