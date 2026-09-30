using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Sandbox.Game.EntityComponents;
using Torch.Managers.PatchManager;
using VRage.Game;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// A grid's power distributor looks for work without locks and lookups when it has none.
    ///
    /// Every frame every grid's distributor (<c>MyResourceDistributorComponent.UpdateBeforeSimulation</c>, on the
    /// parallel updates) first takes the count of its four queues of added and removed sinks and sources - each under a
    /// spin lock - and then, for each resource type, looks the type up in a locked dictionary of pending changes and
    /// again in its index. With nothing queued and nothing to recompute, which is almost always, that was 9.7 s of the
    /// 24 s of grid work on the parallel updates of the old server with nobody on it (dotTrace, 28.09.2026).
    ///
    /// Here, when all four queues are empty (their counts read without the lock: a count read a moment early only puts
    /// the change off to the next frame, as the game's own lock-free readers do), nothing is forced and no type waits for
    /// removal, the method looks only at each type's own "needs recompute" flag and recomputes those types exactly as
    /// the game does. The pending-change counts can only be above zero while a queue holds something, so they have
    /// nothing to add then. Anything else goes the game's way.
    /// </summary>
    [PatchShim]
    public static class DistributorUpdate
    {
        private static Func<MyResourceDistributorComponent, bool> _idle;
        private static Func<MyResourceDistributorComponent, Dictionary<MyDefinitionId, int>> _typeIndex;
        private static Func<MyResourceDistributorComponent, int> _typeGroupCount;
        private static Func<MyResourceDistributorComponent, int, bool> _needsRecompute;

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("DistributorUpdate", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(MyResourceDistributorComponent);
            FieldInfo F(Type owner, string name) => owner.GetField(name, any) ?? throw new MissingFieldException(owner.Name, name);
            var arg = Expression.Parameter(type, "d");

            // the four queues empty (the HashSet inside each concurrent set), nothing forced, no type to remove
            Expression QueueEmpty(string name)
            {
                var set = Expression.Field(arg, F(type, name));
                var inner = Expression.Field(set, F(set.Type, "m_set"));
                return Expression.Equal(Expression.Property(inner, "Count"), Expression.Constant(0));
            }
            var idle = Expression.AndAlso(
                Expression.AndAlso(Expression.AndAlso(QueueEmpty("m_sinksToAdd"), QueueEmpty("m_sinksToRemove")),
                    Expression.AndAlso(QueueEmpty("m_sourcesToAdd"), QueueEmpty("m_sourcesToRemove"))),
                Expression.AndAlso(Expression.Not(Expression.Field(arg, F(type, "m_forceRecalculation"))),
                    Expression.Equal(Expression.Property(Expression.Field(arg, F(type, "m_typesToRemove")), "Count"), Expression.Constant(0))));
            _idle = Expression.Lambda<Func<MyResourceDistributorComponent, bool>>(idle, arg).Compile();
            _typeIndex = Expression.Lambda<Func<MyResourceDistributorComponent, Dictionary<MyDefinitionId, int>>>(
                Expression.Field(arg, F(type, "m_typeIdToIndex")), arg).Compile();
            _typeGroupCount = Expression.Lambda<Func<MyResourceDistributorComponent, int>>(Expression.Field(arg, F(type, "m_typeGroupCount")), arg).Compile();

            // m_dataPerType[index].NeedsRecompute (PerTypeData is a private class)
            var index = Expression.Parameter(typeof(int), "i");
            var list = Expression.Field(arg, F(type, "m_dataPerType"));
            var item = Expression.Property(list, "Item", index);
            _needsRecompute = Expression.Lambda<Func<MyResourceDistributorComponent, int, bool>>(
                Expression.Field(item, F(item.Type, "m_needsRecompute")), arg, index).Compile();

            var update = type.GetMethod(nameof(MyResourceDistributorComponent.UpdateBeforeSimulation), BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)
                         ?? throw new MissingMethodException(type.Name, "UpdateBeforeSimulation");
            ctx.GetPattern(update).Prefixes.Add(typeof(DistributorUpdate).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool Prefix(MyResourceDistributorComponent __instance)
        {
            try
            {
                if (!_idle(__instance)) return true;
                var groups = _typeGroupCount(__instance);
                if (groups <= 0) return false;
                foreach (var pair in _typeIndex(__instance))
                {
                    if (!_needsRecompute(__instance, groups > 1 ? pair.Value : 0)) continue;
                    var typeId = pair.Key;
                    __instance.RecomputeResourceDistribution(ref typeId, false);
                }
                return false;
            }
            catch (InvalidOperationException)
            {
                // the types changed under the loop: the game's way this frame
                return true;
            }
        }
    }
}
