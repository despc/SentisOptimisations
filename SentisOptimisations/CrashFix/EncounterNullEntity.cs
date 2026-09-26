using System;
using System.Reflection;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// A procedural encounter whose asteroid could not be made does not take the server down.
    ///
    /// <c>MyEncounterGenerator.PlaceEncounterToWorld</c> makes an encounter's asteroid with a fixed entity id and hands
    /// what <c>MyWorldGenerator.AddVoxelMap</c> returns straight to <c>RegisterEntityToEncounter</c>; when an entity with
    /// that id is still registered (the previous copy of that asteroid not deleted yet), AddVoxelMap logs a "must
    /// not happen" and returns null, and the registration dereferences it: a NullReferenceException in the session's
    /// update, the server gone (seen on the stand). The null is skipped here - the encounter goes without that
    /// asteroid, which the generator makes again the next time the place is visited.
    /// </summary>
    [PatchShim]
    public static class EncounterNullEntity
    {
        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("EncounterNullEntity", ctx, c =>
        {
            var generator = typeof(Sandbox.Game.World.Generator.MyProceduralWorldGenerator).Assembly
                .GetType("Sandbox.Game.World.Generator.MyEncounterGenerator", true);
            var register = generator.GetMethod("RegisterEntityToEncounter", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                           ?? throw new MissingMethodException("MyEncounterGenerator.RegisterEntityToEncounter");
            c.GetPattern(register).Prefixes.Add(typeof(EncounterNullEntity).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        });

        private static bool Prefix(MyEntity entity)
        {
            if (entity != null) return true;
            SentisOptimisationsPlugin.Log.Warn("An encounter's entity could not be made (its id is taken); the encounter goes without it");
            return false;
        }
    }
}
