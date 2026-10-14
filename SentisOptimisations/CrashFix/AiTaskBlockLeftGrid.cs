using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Torch.Managers.PatchManager;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// An AI task block that left its grid while the grid was in combat no longer takes the server down.
    ///
    /// The server fell on 14.10.2026 (22:19) with a NullReferenceException in <c>MyAiBlockSystem.DeactivateComponent</c>,
    /// called from <c>SetCombatState(true)</c> when an offensive combat block found a target. In combat the grid's AI
    /// system keeps its task block (basic task, recorder) among the active ones, to bring it back when combat ends:
    /// <c>DeactivateComponent</c> removes a task component in combat only when its block is marked for close. A block
    /// that leaves the grid without being closed - broken off by a split, moved by a merge - stays there, of another
    /// grid; when that block is deleted later, its components are cleared and the component's <c>Entity</c> is null.
    /// The next combat ends (the component set active again as it is) and begins: <c>DeactivateComponent</c> reads
    /// <c>Entity.EntityId</c> of it, and the frame throws.
    ///
    /// Fixed at both ends: a block leaving the grid takes its component out of the grid's active ones whatever the
    /// combat state (it is activated on the grid it goes to, as the game does); and an active component without a block,
    /// whichever way it got there, is dropped instead of dereferenced.
    /// </summary>
    [PatchShim]
    public static class AiTaskBlockLeftGrid
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo ActiveField = typeof(MyAiBlockSystem).GetField("m_activeAiComponents", Any);

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("AiTaskBlockLeftGrid", ctx, c =>
        {
            if (ActiveField == null) throw new MissingFieldException("MyAiBlockSystem.m_activeAiComponents");
            var deactivate = typeof(MyAiBlockSystem).GetMethod(nameof(MyAiBlockSystem.DeactivateComponent), Any)
                             ?? throw new MissingMethodException("MyAiBlockSystem.DeactivateComponent");
            var removed = typeof(MyAiBlockSystem).GetMethod("CubeGridOnOnFatBlockRemoved", Any)
                          ?? throw new MissingMethodException("MyAiBlockSystem.CubeGridOnOnFatBlockRemoved");
            c.GetPattern(deactivate).Prefixes.Add(typeof(AiTaskBlockLeftGrid).GetMethod(nameof(DeactivatePrefix), BindingFlags.Static | BindingFlags.NonPublic));
            c.GetPattern(removed).Suffixes.Add(typeof(AiTaskBlockLeftGrid).GetMethod(nameof(RemovedSuffix), BindingFlags.Static | BindingFlags.NonPublic));
        });

        private static Dictionary<MyAiBlockType, IMyAiBlockComponent> Active(MyAiBlockSystem system) =>
            ActiveField.GetValue(system) as Dictionary<MyAiBlockType, IMyAiBlockComponent>;

        private static bool DeactivatePrefix(MyAiBlockSystem __instance, IMyAiBlockComponent component)
        {
            if (component == null) return false;
            var active = Active(__instance);
            if (active == null || !active.TryGetValue(component.AiBlockType, out var current)) return true;
            if (current != null && current.Entity != null && component.Entity != null) return true;
            // (an active component without its block: dropped; the one asked about, if it has no block, has nothing to deactivate)
            if (current == null || current.Entity == null)
            {
                active.Remove(component.AiBlockType);
                SentisOptimisationsPlugin.Log.Warn($"AI {component.AiBlockType} component without a block dropped from the grid's active ones");
            }
            return false;
        }

        private static void RemovedSuffix(MyAiBlockSystem __instance, MyCubeBlock block)
        {
            var active = Active(__instance);
            if (active == null || block == null) return;
            foreach (var type in new List<MyAiBlockType>(active.Keys))
                if (active[type] == null || active[type].Entity == null || active[type].Entity == block)
                    active.Remove(type);
        }
    }
}
