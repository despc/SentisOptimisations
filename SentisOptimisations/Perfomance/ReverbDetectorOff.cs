using System;
using System.Reflection;
using Sandbox.Game.EntityComponents;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The characters' reverb detector left idle on the dedicated server.
    ///
    /// Every character casts a ray a frame around itself (<c>MyEntityReverbDetectorComponent.UpdateBeforeSimulation</c>)
    /// to know how enclosed it is, for the reverb and the ambient sounds of the local player: every reader of the result
    /// (the component itself, ship sounds, weather) asks it of <c>MySession.LocalCharacter</c>, and a dedicated server
    /// has none. On the stand one such update of a player who had just respawned far away took 68.8 ms of a frame
    /// (Watcher, "MyEntityReverbDetectorComponent"). Here the update does nothing on a dedicated server.
    /// </summary>
    [PatchShim]
    public static class ReverbDetectorOff
    {
        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("ReverbDetectorOff", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var update = typeof(MyEntityReverbDetectorComponent).GetMethod("UpdateBeforeSimulation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                         ?? throw new MissingMethodException("MyEntityReverbDetectorComponent.UpdateBeforeSimulation");
            ctx.GetPattern(update).Prefixes.Add(typeof(ReverbDetectorOff).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool Prefix() => !Sandbox.Engine.Platform.Game.IsDedicated;
    }
}
