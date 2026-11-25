using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NLog;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRage.Game;

namespace SentisOptimisationsPlugin.Perfomance
{
    /// <summary>
    /// No projections inside a projection. A blueprint saved off a ship whose projector held a blueprint carries that
    /// blueprint inside, and a projector holding it keeps (and saves) both: the prod's save of 25.11.2026 had 20 such
    /// blueprints in 19 projectors, three deep at times - 76 MB of the 420 MB of the world's entities, written on every
    /// save. A projector's blueprint is kept as it is, only the blueprints of the projectors in it go:
    /// <list type="bullet">
    /// <item>when a blueprint is set into a projector (<c>MyProjectorBase.SetNewBlueprint</c>: loaded by a player, the
    /// projector's own reset);</item>
    /// <item>when a projector is made from its builder (<c>MyProjectorBase.Init</c>: the world loaded, a grid pasted or
    /// welded from a projection) - so the save's old ones go at the next start.</item>
    /// </list>
    /// Harmony, as Multigrid Projector patches <c>Init</c> with Harmony (Torch's and Harmony's patches on one method: only one
    /// works). The live projectors are cleaned without a restart by <c>!projections nested [apply]</c>. A projector built
    /// from a projection comes empty then - load its blueprint into it again.
    /// </summary>
    [PatchShim]
    public static class NestedProjections
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly Harmony Harmony = new Harmony("SentisOptimisations.NestedProjections");

        /// <summary>Blueprints taken out of projectors' blueprints since the start (for the log and the checks).</summary>
        public static int Stripped;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("NestedProjections", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var set = typeof(MyProjectorBase).GetMethod("SetNewBlueprint", instance, null, new[] { typeof(List<MyObjectBuilder_CubeGrid>) }, null)
                      ?? throw new MissingMethodException("MyProjectorBase", "SetNewBlueprint");
            var init = typeof(MyProjectorBase).GetMethod("Init", instance, null, new[] { typeof(MyObjectBuilder_CubeBlock), typeof(Sandbox.Game.Entities.MyCubeGrid) }, null)
                       ?? throw new MissingMethodException("MyProjectorBase", "Init");
            Harmony.Patch(set, prefix: new HarmonyMethod(typeof(NestedProjections).GetMethod(nameof(SetPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            Harmony.Patch(init, prefix: new HarmonyMethod(typeof(NestedProjections).GetMethod(nameof(InitPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        /// <summary>
        /// The blueprints of the projectors in these grids taken out; how many blueprints went (a projector's list counts
        /// each of its grids). The grids themselves stay.
        /// </summary>
        public static int Strip(IEnumerable<MyObjectBuilder_CubeGrid> grids)
        {
            if (grids == null) return 0;
            var removed = 0;
            foreach (var grid in grids)
            {
                if (grid?.CubeBlocks == null) continue;
                foreach (var block in grid.CubeBlocks)
                {
                    if (!(block is MyObjectBuilder_ProjectorBase projector)) continue;
                    var count = (projector.ProjectedGrids?.Count ?? 0) + (projector.ProjectedGrid != null ? 1 : 0);
                    if (count == 0) continue;
                    projector.ProjectedGrids = null;
                    projector.ProjectedGrid = null;
                    removed += count;
                }
            }
            return removed;
        }

        /// <summary>The same for a projector's own builder: the blueprints inside its blueprint.</summary>
        public static int StripInside(MyObjectBuilder_ProjectorBase projector)
        {
            if (projector == null) return 0;
            var removed = Strip(projector.ProjectedGrids);
            if (projector.ProjectedGrid != null) removed += Strip(new[] { projector.ProjectedGrid });
            return removed;
        }

        private static void SetPrefix(MyProjectorBase __instance, List<MyObjectBuilder_CubeGrid> gridBuilders)
        {
            try
            {
                Note(__instance, Strip(gridBuilders), "set");
            }
            catch (Exception e)
            {
                Log.Error(e, "NestedProjections: a blueprint set into a projector not cleaned");
            }
        }

        private static void InitPrefix(MyProjectorBase __instance, MyObjectBuilder_CubeBlock objectBuilder)
        {
            try
            {
                Note(__instance, StripInside(objectBuilder as MyObjectBuilder_ProjectorBase), "loaded");
            }
            catch (Exception e)
            {
                Log.Error(e, "NestedProjections: a projector's builder not cleaned");
            }
        }

        private static void Note(MyProjectorBase projector, int removed, string how)
        {
            if (removed <= 0) return;
            Stripped += removed;
            if (global::SentisOptimisations.DiagLog.On)
                Log.Info($"NestedProjections: {removed} blueprints of projectors inside the blueprint of a projector {how} ({projector.EntityId}) taken out");
        }
    }
}
