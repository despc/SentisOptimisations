using System;
using System.Reflection;
using HarmonyLib;
using Microsoft.CodeAnalysis;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using SentisOptimisationsPlugin.CrashFix;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using VRage.Scripting.CompilerMethods;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// Work the game does for a player who is looking at the screen, skipped on a server that has no
    /// screen, plus two crashes that a dedicated server should survive.
    ///
    /// Skipped outright:
    /// <list type="bullet">
    /// <item><c>MyEntity3DSoundEmitter.Update</c> - positional audio, updated per emitter per frame;</item>
    /// <item><c>MyThrust.RenderUpdate</c> - the thruster flame, its colour and its light;</item>
    /// <item><c>MyCharacter.UpdateHeadAndWeapon</c> - the animation of the head and of the held tool,
    /// which the server does not draw.</item>
    /// </list>
    ///
    /// Left running but not allowed to take the server down (their exceptions are swallowed):
    /// <c>MyTextPanelComponent.SetDefaultTexture</c> and <c>MyAutopilotComponent.UpdateAutopilot</c>.
    ///
    /// And <c>PerfCountingRewriter.Rewrite</c> returns the script unchanged: it instruments every mod
    /// script with per-method counters for a profiler that a dedicated server does not run.
    /// </summary>
    [PatchShim]
    public static class ParallelUpdateTweaks
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("ParallelUpdateTweaks", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var skip = typeof(ParallelUpdateTweaks).GetMethod(nameof(Skip), BindingFlags.Static | BindingFlags.NonPublic);

            foreach (var target in new[]
                     {
                         typeof(MyEntity3DSoundEmitter).GetMethod(nameof(MyEntity3DSoundEmitter.Update),
                             BindingFlags.Instance | BindingFlags.Public),
                         typeof(MyThrust).GetMethod("RenderUpdate",
                             BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
                         typeof(MyCharacter).GetMethod("UpdateHeadAndWeapon", BindingFlags.Instance | BindingFlags.NonPublic),
                     })
            {
                if (target == null) throw new InvalidOperationException("ParallelUpdateTweaks: a skipped method is gone");
                ctx.GetPattern(target).Prefixes.Add(skip);
            }

            var rewriter = typeof(ModPerfCounter).Assembly.GetType("VRage.Scripting.Rewriters.PerfCountingRewriter");
            var rewrite = rewriter?.GetMethod("Rewrite", BindingFlags.Static | BindingFlags.Public);
            if (rewrite == null) throw new MissingMethodException("PerfCountingRewriter.Rewrite");
            ctx.GetPattern(rewrite).Prefixes.Add(
                typeof(ParallelUpdateTweaks).GetMethod(nameof(RewritePatched), BindingFlags.Static | BindingFlags.NonPublic));

            var finalizer = new HarmonyMethod(typeof(CrashFixPatch).GetMethod(
                nameof(CrashFixPatch.SuppressExceptionFinalizer), any));
            CrashFixPatch.harmony.Patch(
                typeof(MyTextPanelComponent).GetMethod(nameof(MyTextPanelComponent.SetDefaultTexture),
                    BindingFlags.Instance | BindingFlags.Public), finalizer: finalizer);
            CrashFixPatch.harmony.Patch(
                typeof(MyAutopilotComponent).GetMethod(nameof(MyAutopilotComponent.UpdateAutopilot),
                    BindingFlags.Instance | BindingFlags.Public), finalizer: finalizer);
        }

        /// <summary>Skips the patched method.</summary>
        private static bool Skip() => false;

        /// <summary>Hands the script back as it is, without the profiler's counters.</summary>
        private static bool RewritePatched(SyntaxTree syntaxTree, ref SyntaxTree __result)
        {
            __result = syntaxTree;
            return false;
        }
    }
}
