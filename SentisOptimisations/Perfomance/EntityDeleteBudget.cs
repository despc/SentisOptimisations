using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The entities closed in a frame deleted within a few milliseconds of it, the rest in the frames after.
    ///
    /// <c>MyEntities.DeleteRememberedEntities</c>, at the end of every frame's entity update, deletes everything closed
    /// since the last one: a global encounter that goes (a dozen grids, 2400 blocks) was 67 ms of one frame on the
    /// stand. Here, in that frame-end call only, the deleting stops once <see cref="BudgetMs"/> are spent (one entity at
    /// least), and the rest wait in the set the game itself keeps for the next frame (where it puts a pinned entity) -
    /// closed already, only their removal a frame or a few later. Any other call (the creation thread's, which deletes
    /// until none are left; the unloading) is the game's own.
    /// </summary>
    [PatchShim]
    public static class EntityDeleteBudget
    {
        /// <summary>The deleting a frame-end call does before the rest waits for the next frame.</summary>
        public const double BudgetMs = 4;

        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
        private static FieldInfo _toDelete, _nextFrame;
        [ThreadStatic] private static bool _inFrameEnd;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("EntityDeleteBudget", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(MyEntities);
            _toDelete = type.GetField("m_entitiesToDelete", statics) ?? throw new MissingFieldException("MyEntities.m_entitiesToDelete");
            _nextFrame = type.GetField("m_entitiesToDeleteNextFrame", statics) ?? throw new MissingFieldException("MyEntities.m_entitiesToDeleteNextFrame");
            var delete = type.GetMethod("DeleteRememberedEntities", statics, null, Type.EmptyTypes, null) ?? throw new MissingMethodException("MyEntities.DeleteRememberedEntities");
            var after = type.GetMethod("UpdateAfterSimulation", statics, null, Type.EmptyTypes, null) ?? throw new MissingMethodException("MyEntities.UpdateAfterSimulation");
            MethodInfo Own(string name) => typeof(EntityDeleteBudget).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            ctx.GetPattern(after).Prefixes.Add(Own(nameof(AfterPrefix)));
            ctx.GetPattern(after).Suffixes.Add(Own(nameof(AfterSuffix)));
            ctx.GetPattern(delete).Prefixes.Add(Own(nameof(DeletePrefix)));
        }

        private static void AfterPrefix() => _inFrameEnd = true;
        private static void AfterSuffix() => _inFrameEnd = false;

        /// <summary>How many of the waiting ones a call deletes: all while there is time, the first always.</summary>
        public static bool GoOn(int deleted, long elapsedTicks, long budgetTicks) => deleted == 0 || elapsedTicks < budgetTicks;

        private static bool DeletePrefix()
        {
            if (!_inFrameEnd || MySession.Static?.Ready != true) return true;
            var toDelete = (HashSet<MyEntity>)_toDelete.GetValue(null);
            if (toDelete == null || toDelete.Count <= 1) return true;
            var nextFrame = (HashSet<MyEntity>)_nextFrame.GetValue(null);
            var started = Stopwatch.GetTimestamp();
            // no more than the frame has left (one entity always, as before)
            var budget = (long)(Optimizer.Optimizations.FrameClock.Allowed(BudgetMs, 0) * Stopwatch.Frequency / 1000);
            var deleted = 0;
            MyEntities.CloseAllowed = true;
            try
            {
                // the game's own loop, but for the time it may take
                while (toDelete.Count > 0 && GoOn(deleted, Stopwatch.GetTimestamp() - started, budget))
                {
                    using (MyEntities.EntityCloseLock.AcquireExclusiveUsing())
                    {
                        var entity = toDelete.First();
                        if (!entity.Pinned)
                        {
                            RaiseDelete(entity);
                            entity.Delete();
                        }
                        else
                        {
                            MyEntities.Remove(entity);
                            toDelete.Remove(entity);
                            nextFrame.Add(entity);
                        }
                        deleted++;
                    }
                }
                // what time did not allow: next frame, with the pinned ones
                if (toDelete.Count > 0)
                    if (global::SentisOptimisations.DiagLog.On) Log.Info($"EntityDeleteBudget: {deleted} deleted in {(Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency:0.0} ms, {toDelete.Count} left for the next frame");
                foreach (var entity in toDelete) nextFrame.Add(entity);
                toDelete.Clear();
            }
            finally
            {
                MyEntities.CloseAllowed = false;
            }
            _toDelete.SetValue(null, nextFrame);
            _nextFrame.SetValue(null, toDelete);
            return false;
        }

        private static FieldInfo _onEntityDelete;

        /// <summary><c>MyEntities.OnEntityDelete?.Invoke(entity)</c>, as the game's loop does.</summary>
        private static void RaiseDelete(MyEntity entity)
        {
            if (_onEntityDelete == null)
                _onEntityDelete = typeof(MyEntities).GetField("OnEntityDelete", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            (_onEntityDelete?.GetValue(null) as Action<MyEntity>)?.Invoke(entity);
        }
    }
}
