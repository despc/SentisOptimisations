using System;
using System.Reflection;
using Sandbox;
using Sandbox.Game.Entities.Character;
using Torch.Managers.PatchManager;
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin.Freezer
{
    /// <summary>
    /// A character seated on a frozen grid that is killed gets its updates back, so its death runs to the end.
    ///
    /// The freezer takes a frozen grid off the updates with everything in its hierarchy, and a seated character is a
    /// child of its seat (<c>MyCockpit</c> adds the pilot to its hierarchy). But its stats are updated apart from the
    /// entity (<c>MyEntityStatComponent</c> is a <c>MyUpdatingEntityComponent</c>, run by the session's
    /// <c>MyEntityComponentUpdater</c>): food runs out and hunger hits on a frozen grid. At zero health <c>Kill</c> only
    /// raises <c>m_dieAfterSimulation</c>; the death itself (out of the seat, off the save, the body) is done by the
    /// character's own <c>UpdateAfterSimulation</c>, which a frozen character never gets. Such a character was saved
    /// seated with zero health and starved again after every load (prod 22.10 and 29.10.2026: the same offline players
    /// in respawn rovers "died of hunger" after each restart, DeathCountDict announcing it every time).
    ///
    /// Only the dying character is thawed: a seated character kept frozen does not breathe its seat's air or run its
    /// suit, and the rest of the grid stays frozen.
    /// </summary>
    [PatchShim]
    public static class FrozenPilotDeath
    {
        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("FrozenPilotDeath", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var kill = typeof(MyCharacter).GetMethod(nameof(MyCharacter.Kill), BindingFlags.Instance | BindingFlags.Public,
                           null, new[] { typeof(bool), typeof(MyDamageInformation) }, null)
                       ?? throw new MissingMethodException(nameof(MyCharacter), nameof(MyCharacter.Kill));
            ctx.GetPattern(kill).Suffixes.Add(
                typeof(FrozenPilotDeath).GetMethod(nameof(Suffix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        // Kill comes from wherever the damage did: hunger is invoked on the game thread, a stat logic's damage may run
        // on the parallel stat updates. The update lists are touched on the game thread only.
        private static void Suffix(MyCharacter __instance)
        {
            if (FrozenUpdateGuard.Admit(__instance.Flags)) return;
            MySandboxGame.Static.Invoke(() => Thaw(__instance), "FrozenPilotDeath");
        }

        private static void Thaw(MyCharacter character)
        {
            if (character.Closed || FrozenUpdateGuard.Admit(character.Flags)) return;
            FreezeLogic.RegisterRecursive(character);
            FreezeLogic.Log("Thawed a dying character on a frozen grid: " + character.DisplayName);
        }
    }
}
