using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game.AI.Pathfinding;
using SpaceEngineers.Game.AI;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// A wolf or a spider looking for a target finds its way to a player only when that player could become its target.
    ///
    /// The animals' behaviour trees look for a target every 11th frame (<c>MyWolfActions.GetTargetWithPriority</c>, a
    /// sphere of 200-400 m): for every player in it standing on something the animal asks the navigation mesh for a
    /// path from itself to the player (<c>IMyPathfinding.ReachableUnderThreshold</c>, Recast in native code), and only
    /// then compares the player's priority (6) with the one it has already got (1 for a player taken a moment before,
    /// 2-5 for a grid's block): the path was found for every player in reach, each time, and thrown away for all but one.
    /// It was the most of the animals' cost on the prod: 634 ms of 4.9 s of the server's frames over 36 s, all of it in
    /// the path search (29.10.2026).
    ///
    /// Here the priorities are compared first and the path is asked for only when the player would be taken; what the
    /// animal ends up with is the game's.
    /// </summary>
    [PatchShim]
    public static class WolfTargetPath
    {
        private static readonly Harmony Harmony = new Harmony("SentisOptimisations.WolfTargetPath");

        // Read by SentisTests.
        public static long Paths;
        public static long Skipped;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("WolfTargetPath", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var method = typeof(MyWolfActions).GetMethod("GetTargetWithPriority", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                         ?? throw new MissingMethodException("MyWolfActions.GetTargetWithPriority");
            // by Harmony: nobody else patches the method, and the transpiler copies the locals' loads as they are
            Harmony.Patch(method, transpiler: new HarmonyMethod(typeof(WolfTargetPath).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        // ReachableUnderThreshold(begin, end, 1f) followed by "brfalse skip; ldloc priority; ldloc best; bge skip" (the two
        // jumps to one place, under two labels of Harmony's): the call made a call of Reachable with the two locals loaded
        // before it
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var found = 0;
            for (var i = 0; i + 4 < list.Count; i++)
            {
                if (!(list[i].operand is MethodInfo called) || called.Name != "ReachableUnderThreshold" || called.GetParameters().Length != 3) continue;
                if (!(list[i + 1].opcode == OpCodes.Brfalse || list[i + 1].opcode == OpCodes.Brfalse_S) || !list[i + 2].IsLdloc() || !list[i + 3].IsLdloc() ||
                    !(list[i + 4].opcode == OpCodes.Bge || list[i + 4].opcode == OpCodes.Bge_S)) continue;
                var call = new CodeInstruction(OpCodes.Call, typeof(WolfTargetPath).GetMethod(nameof(Reachable), BindingFlags.Static | BindingFlags.NonPublic));
                call.blocks.AddRange(list[i].blocks);
                var loadPriority = new CodeInstruction(list[i + 2].opcode, list[i + 2].operand);
                var loadBest = new CodeInstruction(list[i + 3].opcode, list[i + 3].operand);
                // a jump to the call lands on the loads before it
                loadPriority.labels.AddRange(list[i].labels);
                list[i] = call;
                list.InsertRange(i, new[] { loadPriority, loadBest });
                found++;
            }
            if (found != 1)
            {
                var at = list.FindIndex(c => c.operand is MethodInfo m && m.Name == "ReachableUnderThreshold");
                SentisOptimisationsPlugin.Log.Error($"WolfTargetPath: {found} reachability checks before a priority check in GetTargetWithPriority, not 1; left as it is" +
                                                    (at < 0 ? "" : ": " + string.Join(" | ", list.Skip(at).Take(5).Select(c => c.ToString()))));
                return instructions;
            }
            return list;
        }

        private static bool Reachable(IMyPathfinding pathfinding, Vector3D begin, IMyDestinationShape end, float threshold, int priority, int best)
        {
            if (priority >= best)
            {
                Skipped++;
                return false;
            }
            Paths++;
            return pathfinding.ReachableUnderThreshold(begin, end, threshold);
        }
    }
}
