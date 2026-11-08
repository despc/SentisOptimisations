using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Microsoft.CodeAnalysis;
using NLog;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using SentisOptimisationsPlugin.CrashFix;
using Torch.Managers.PatchManager;
using VRage.Audio;
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
    /// <item><c>MyThrust.RenderUpdate</c> - the thruster flame, its colour and its light (the flag that
    /// asks for it is still cleared, so the thruster's ten-frame update turns off as in vanilla);</item>
    /// <item><c>MyCharacter.UpdateHeadAndWeapon</c> - the animation of the head and of the held tool,
    /// which the server does not draw.</item>
    /// </list>
    ///
    /// Left running but not allowed to take the server down (their exceptions are swallowed):
    /// <c>MyTextPanelComponent.SetDefaultTexture</c> and <c>MyAutopilotComponent.UpdateAutopilot</c>.
    ///
    /// The emitter's <c>PlaySound</c>, <c>PlaySingleSound</c> and <c>PlaySoundWithDistance</c> called off the game
    /// thread return false at once, as in vanilla, but without its warning: blocks updated on other threads (the
    /// turrets' aiming sound, welders, grinders, searchlights) wrote "Sound on different thread" on every call, and
    /// NLog's filter dropped each line only after evaluating its conditions - 3.4 s of 57 on prod (07.11.2026).
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
                         typeof(MyCharacter).GetMethod("UpdateHeadAndWeapon", BindingFlags.Instance | BindingFlags.NonPublic),
                     })
            {
                if (target == null) throw new InvalidOperationException("ParallelUpdateTweaks: a skipped method is gone");
                ctx.GetPattern(target).Prefixes.Add(skip);
            }

            var offThread = typeof(ParallelUpdateTweaks).GetMethod(nameof(SoundOffThread), BindingFlags.Static | BindingFlags.NonPublic);
            foreach (var target in new[]
                     {
                         typeof(MyEntity3DSoundEmitter).GetMethod(nameof(MyEntity3DSoundEmitter.PlaySound),
                             new[] { typeof(MySoundPair), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool?), typeof(bool) }),
                         typeof(MyEntity3DSoundEmitter).GetMethod(nameof(MyEntity3DSoundEmitter.PlaySingleSound),
                             new[] { typeof(MySoundPair), typeof(bool), typeof(bool), typeof(bool), typeof(bool?) }),
                         typeof(MyEntity3DSoundEmitter).GetMethod(nameof(MyEntity3DSoundEmitter.PlaySoundWithDistance),
                             new[] { typeof(MyCueId), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool?) }),
                     })
            {
                if (target == null || target.ReturnType != typeof(bool)) throw new InvalidOperationException("ParallelUpdateTweaks: a sound emitter's play method is gone");
                ctx.GetPattern(target).Prefixes.Add(offThread);
            }

            var thrustRender = typeof(MyThrust).GetMethod("RenderUpdate",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (thrustRender == null) throw new InvalidOperationException("ParallelUpdateTweaks: MyThrust.RenderUpdate is gone");
            ctx.GetPattern(thrustRender).Prefixes.Add(
                typeof(ParallelUpdateTweaks).GetMethod(nameof(SkipThrustRender), BindingFlags.Static | BindingFlags.NonPublic));

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

        /// <summary>Off the game thread: false, as vanilla returns there, without its warning in the log.</summary>
        private static bool SoundOffThread(ref bool __result)
        {
            var game = MySandboxGame.Static?.UpdateThread;
            if (game == null || Thread.CurrentThread == game) return true;
            __result = false;
            return false;
        }

        private static readonly Action<MyThrust, bool> SetRenderNeedsUpdate =
            Accessors.SetField<MyThrust, bool>("m_renderNeedsUpdate");

        /// <summary>
        /// Skips the flame but still marks it done, as the end of the method would. The thruster keeps its
        /// ten-frame update only while that mark is set; left set, every thruster in the world ran it forever -
        /// 2469 calls a frame, 0.68 ms, for 128 Spitfires on the stand.
        /// </summary>
        private static bool SkipThrustRender(MyThrust __instance)
        {
            SetRenderNeedsUpdate(__instance, false);
            return false;
        }

        /// <summary>Hands the script back as it is, without the profiler's counters.</summary>
        private static bool RewritePatched(SyntaxTree syntaxTree, ref SyntaxTree __result)
        {
            __result = syntaxTree;
            return false;
        }
    }
}
