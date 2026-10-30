using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Torch.Managers.PatchManager;
using Ingame = Sandbox.ModAPI.Ingame;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// A script's search of the blocks by name (<c>GridTerminalSystem.SearchBlocksOfName</c>, <c>GetBlockWithName</c>)
    /// answered from what the same search found before, while the blocks and their names are the same.
    ///
    /// The game goes through every block of the terminal system (the whole grid with all joined to it) on every call: the
    /// name of each made into a new string and searched without regard to case - on .NET Framework that is the slow
    /// culture-aware path for names in Russian. Scripts call it on every run: Whiplash's Weapon Sequencing, run by timers
    /// in a loop, 1.65 s of the 3.7 s all the scripts took in 3 minutes on the stand's copy of the prod (dotTrace,
    /// 30.10.2026) - half of the scripts' time in this one call.
    ///
    /// Here the blocks whose names hold the asked name are kept for the system and the name, found the game's way (the
    /// same comparison, the same order of the blocks). A call then looks only at them: whether each is open to scripts
    /// now and what the script's own filter says are asked each time, as the game asks them. What is kept goes when a
    /// block joins or leaves the system (<c>MyGridTerminalSystem.Add</c>/<c>Remove</c>, grids joined, split, built,
    /// ground) or a block of its grid is renamed (every rename ends in <c>MyTerminalBlock.DisplayNameText</c>'s setter;
    /// a block's default name is made the first time it is read, which the first search does, and stays).
    /// </summary>
    [PatchShim]
    public static class TerminalNameSearch
    {
        /// <summary>Names kept for a system at most; past that its names are dropped and found again.</summary>
        private const int MaxNames = 64;

        private sealed class Found
        {
            public readonly Dictionary<string, List<MyTerminalBlock>> Containing = new Dictionary<string, List<MyTerminalBlock>>(StringComparer.Ordinal);
            public readonly Dictionary<string, List<MyTerminalBlock>> Named = new Dictionary<string, List<MyTerminalBlock>>(StringComparer.Ordinal);
        }

        private static readonly ConditionalWeakTable<MyGridTerminalSystem, Found> Systems = new ConditionalWeakTable<MyGridTerminalSystem, Found>();
        private static readonly FieldInfo BlocksField = typeof(MyGridTerminalSystem).GetField("m_blocks", BindingFlags.Instance | BindingFlags.NonPublic);

        // Read by SentisTests: searches answered from what was kept, and searches that went through the blocks.
        public static long Kept, Searched;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("TerminalNameSearch", ctx, c =>
        {
            if (BlocksField == null) throw new MissingFieldException("MyGridTerminalSystem.m_blocks");
            const BindingFlags own = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
            MethodInfo Explicit(string name) => typeof(MyGridTerminalSystem).GetMethods(own)
                                                    .SingleOrDefault(m => m.Name == "Sandbox.ModAPI.Ingame.IMyGridTerminalSystem." + name)
                                                ?? throw new MissingMethodException("MyGridTerminalSystem: Ingame " + name);
            c.GetPattern(Explicit("SearchBlocksOfName")).Prefixes.Add(Own(nameof(SearchPrefix)));
            c.GetPattern(Explicit("GetBlockWithName")).Prefixes.Add(Own(nameof(WithNamePrefix)));
            foreach (var name in new[] { "Add", "Remove" })
                c.GetPattern(typeof(MyGridTerminalSystem).GetMethod(name, own, null, new[] { typeof(MyTerminalBlock) }, null)
                             ?? throw new MissingMethodException("MyGridTerminalSystem." + name))
                    .Suffixes.Add(Own(nameof(SystemChanged)));
            // the renames: the terminal block's setter, and any of a block type of its own that would not go through it
            foreach (var type in typeof(MyTerminalBlock).Assembly.GetTypes().Where(t => typeof(MyTerminalBlock).IsAssignableFrom(t)))
            {
                var setter = type.GetProperty("DisplayNameText", own)?.GetSetMethod(true);
                if (setter != null && !setter.IsAbstract) c.GetPattern(setter).Suffixes.Add(Own(nameof(Renamed)));
            }
        });

        private static MethodInfo Own(string name) => typeof(TerminalNameSearch).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        private static void SystemChanged(MyGridTerminalSystem __instance) => Drop(__instance);

        private static void Renamed(object __instance) => Drop((__instance as MyTerminalBlock)?.CubeGrid?.GridSystems?.TerminalSystem);

        private static void Drop(MyGridTerminalSystem system)
        {
            if (system == null || !Systems.TryGetValue(system, out var found)) return;
            lock (found)
            {
                found.Containing.Clear();
                found.Named.Clear();
            }
        }

        /// <summary>The blocks of the system the game's search would pick by the name, in the game's order.</summary>
        private static List<MyTerminalBlock> Blocks(MyGridTerminalSystem system, string name, bool containing)
        {
            var found = Systems.GetValue(system, _ => new Found());
            lock (found)
            {
                var kept = containing ? found.Containing : found.Named;
                if (kept.TryGetValue(name, out var list))
                {
                    System.Threading.Interlocked.Increment(ref Kept);
                    return list;
                }
                System.Threading.Interlocked.Increment(ref Searched);
                list = new List<MyTerminalBlock>();
                foreach (var block in (HashSet<MyTerminalBlock>)BlocksField.GetValue(system))
                    if (containing ? System.StringExtensions.Contains(block.CustomName.ToString(), name, StringComparison.OrdinalIgnoreCase)
                                   : block.CustomName.CompareTo(name) == 0)
                        list.Add(block);
                if (kept.Count >= MaxNames) kept.Clear();
                kept[name] = list;
                return list;
            }
        }

        // the game's SearchBlocksOfName for scripts: blocks cleared, then each block whose name holds the name, open to
        // scripts and taken by the script's filter, added in the order of the system's blocks
        private static bool SearchPrefix(MyGridTerminalSystem __instance, string name, List<Ingame.IMyTerminalBlock> blocks, Func<Ingame.IMyTerminalBlock, bool> collect)
        {
            if (name == null) return true;
            var found = Blocks(__instance, name, true);
            blocks?.Clear();
            foreach (var block in found)
                if (block.IsAccessibleForProgrammableBlock && (collect == null || collect(block)))
                    blocks?.Add(block);
            return false;
        }

        // the game's GetBlockWithName for scripts: the first block of that very name open to scripts
        private static bool WithNamePrefix(MyGridTerminalSystem __instance, string name, ref Ingame.IMyTerminalBlock __result)
        {
            if (name == null) return true;
            __result = null;
            foreach (var block in Blocks(__instance, name, false))
                if (block.IsAccessibleForProgrammableBlock)
                {
                    __result = block;
                    break;
                }
            return false;
        }
    }
}
