using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The entities that moved in a frame, for the game's pruning structure, kept in two reused lists.
    ///
    /// Every entity that moves (every flying grid, character, floating object, every frame) is put by
    /// <c>MyGamePruningStructure.MoveEntity</c> into a <c>ConcurrentBag</c>: a new node per entry. Once a frame the bag
    /// is walked, and walking a ConcurrentBag copies it into a new array first. Nearly two hundred thousand nodes and
    /// twenty thousand arrays between two young collections (heap dump), garbage for nothing.
    ///
    /// Here: a list under a lock takes the moves (from the game thread and the parallel updates alike), and once a
    /// frame it is swapped with a second one, which is walked and cleared. Unlike the vanilla drain after the walk,
    /// a move made while the frame's list is walked is not dropped - it waits for the next frame.
    /// </summary>
    [PatchShim]
    public static class PruningMovedList
    {
        private static readonly object Lock = new object();
        private static List<MyEntity> _moved = new List<MyEntity>();
        private static List<MyEntity> _walked = new List<MyEntity>();
        private static readonly HashSet<MyEntity> Seen = new HashSet<MyEntity>();
        private static Action<MyGamePruningStructure, MyEntity> _moveInternal;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("PruningMovedList", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(MyGamePruningStructure);
            var moveInternal = type.GetMethod("MoveInternal", instance, null, new[] { typeof(MyEntity) }, null) ?? throw new MissingMethodException("MyGamePruningStructure.MoveInternal");
            var moveEntity = type.GetMethod("MoveEntity", instance, null, new[] { typeof(MyEntity) }, null) ?? throw new MissingMethodException("MyGamePruningStructure.MoveEntity");
            var update = type.GetMethod("Update", instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) ?? throw new MissingMethodException("MyGamePruningStructure.Update");
            var clear = type.GetMethod("Clear", instance, null, Type.EmptyTypes, null) ?? throw new MissingMethodException("MyGamePruningStructure.Clear");
            _moveInternal = (Action<MyGamePruningStructure, MyEntity>)moveInternal.CreateDelegate(typeof(Action<MyGamePruningStructure, MyEntity>));
            MethodInfo Own(string name) => typeof(PruningMovedList).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            ctx.GetPattern(moveEntity).Prefixes.Add(Own(nameof(MoveEntityPrefix)));
            ctx.GetPattern(update).Prefixes.Add(Own(nameof(UpdatePrefix)));
            ctx.GetPattern(clear).Suffixes.Add(Own(nameof(ClearSuffix)));
        }

        private static bool MoveEntityPrefix(MyEntity entity)
        {
            lock (Lock) _moved.Add(entity);
            return false;
        }

        /// <summary>Game thread, once a frame: every entity that moved, once, into its place in the trees.</summary>
        private static bool UpdatePrefix(MyGamePruningStructure __instance)
        {
            lock (Lock)
            {
                var swap = _moved;
                _moved = _walked;
                _walked = swap;
            }
            try
            {
                foreach (var entity in _walked)
                    if (Seen.Add(entity)) _moveInternal(__instance, entity);
            }
            finally
            {
                Seen.Clear();
                _walked.Clear();
            }
            return false;
        }

        private static void ClearSuffix()
        {
            lock (Lock) _moved.Clear();
            _walked.Clear();
            Seen.Clear();
        }
    }
}
