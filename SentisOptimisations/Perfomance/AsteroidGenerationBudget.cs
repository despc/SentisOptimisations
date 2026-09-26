using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Sandbox.Game.World.Generator;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The procedural asteroids round a spot a player arrives at, made a few per frame instead of all in one.
    ///
    /// A player who jumps (or flies fast) into space nobody has been to gets every asteroid of the new cells made at
    /// once: the shape of each worked out and a voxel map added, a dozen and more in a single frame - 50 to 140 ms per
    /// jump on the stand (procedural_jump), seconds when many players arrive at new places together (5.6 s in
    /// freezer_stress).
    ///
    /// Here the asteroid module's <c>GenerateObjects</c> queues the seeds it is given, and the queue is worked off within
    /// <see cref="BudgetMs"/> a frame (at least one asteroid a frame), each seed by the game's own method, alone. The
    /// asteroids so come a few frames later - kilometres away from the player. A seed whose cell the generator has let
    /// go meanwhile (the player gone on) is dropped, as the game would never have made it; one made meanwhile (another
    /// player) is skipped, as the game skips it.
    /// </summary>
    [PatchShim]
    public static class AsteroidGenerationBudget
    {
        private const double BudgetMs = 3;

        private sealed class Pending
        {
            public MyProceduralAsteroidCellGenerator Module;
            public MyObjectSeed Seed;
            public HashSet<MyObjectSeedParams> Existing;
            public MyEntity Tracked;
        }

        private static readonly Queue<Pending> Queue = new Queue<Pending>();
        private static readonly HashSet<MyObjectSeed> Queued = new HashSet<MyObjectSeed>();
        private static readonly List<MyObjectSeed> One = new List<MyObjectSeed>(1);
        private static MethodInfo _generateObjects;
        private static FieldInfo _cells;
        private static bool _inner;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("AsteroidGenerationBudget", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _generateObjects = typeof(MyProceduralAsteroidCellGenerator).GetMethod("GenerateObjects", instance | BindingFlags.DeclaredOnly)
                               ?? throw new MissingMethodException("MyProceduralAsteroidCellGenerator.GenerateObjects");
            _cells = typeof(MyProceduralWorldModule).GetField("m_cells", instance) ?? throw new MissingFieldException("MyProceduralWorldModule.m_cells");
            if (_cells.FieldType != typeof(Dictionary<Vector3I, MyProceduralCell>)) throw new InvalidOperationException("MyProceduralWorldModule.m_cells is not what AsteroidGenerationBudget knows");
            var update = typeof(MyProceduralWorldGenerator).GetMethod("UpdateBeforeSimulation", instance | BindingFlags.DeclaredOnly)
                         ?? throw new MissingMethodException("MyProceduralWorldGenerator.UpdateBeforeSimulation");
            var unload = typeof(MyProceduralWorldGenerator).GetMethod("UnloadData", instance | BindingFlags.DeclaredOnly);
            MethodInfo Own(string name) => typeof(AsteroidGenerationBudget).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            ctx.GetPattern(_generateObjects).Prefixes.Add(Own(nameof(GenerateObjectsPrefix)));
            ctx.GetPattern(update).Suffixes.Add(Own(nameof(Drain)));
            if (unload != null) ctx.GetPattern(unload).Prefixes.Add(Own(nameof(Clear)));
        }

        private static bool GenerateObjectsPrefix(MyProceduralAsteroidCellGenerator __instance, List<MyObjectSeed> objectsList,
            HashSet<MyObjectSeedParams> existingObjectsSeeds, MyEntity trackedEntity)
        {
            if (_inner) return true;
            foreach (var seed in objectsList)
            {
                if (seed.Params.Generated || existingObjectsSeeds.Contains(seed.Params) || !Queued.Add(seed)) continue;
                Queue.Enqueue(new Pending { Module = __instance, Seed = seed, Existing = existingObjectsSeeds, Tracked = trackedEntity });
            }
            return false;
        }

        /// <summary>Game thread, after the generator's update: seeds made within the budget.</summary>
        private static void Drain()
        {
            if (Queue.Count == 0) return;
            var started = Stopwatch.GetTimestamp();
            var budget = (long)(BudgetMs * Stopwatch.Frequency / 1000);
            do
            {
                var next = Queue.Dequeue();
                Queued.Remove(next.Seed);
                if (next.Seed.Params.Generated) continue;
                var cells = (Dictionary<Vector3I, MyProceduralCell>)_cells.GetValue(next.Module);
                if (!cells.TryGetValue(next.Seed.CellId, out var cell) || !ReferenceEquals(cell, next.Seed.Cell)) continue;
                One.Clear();
                One.Add(next.Seed);
                _inner = true;
                try { _generateObjects.Invoke(next.Module, new object[] { One, next.Existing, next.Tracked }); }
                catch (Exception e) { SentisOptimisationsPlugin.Log.Error(e, "AsteroidGenerationBudget: an asteroid failed"); }
                finally
                {
                    _inner = false;
                    One.Clear();
                }
            } while (Queue.Count > 0 && Stopwatch.GetTimestamp() - started < budget);
        }

        private static void Clear()
        {
            Queue.Clear();
            Queued.Clear();
        }
    }
}
