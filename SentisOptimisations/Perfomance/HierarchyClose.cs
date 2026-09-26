using System;
using System.Reflection;
using Torch.Managers.PatchManager;
using VRage.Game.Components;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The blocks of a closing grid do not unsubscribe from the grid's component events one by one.
    ///
    /// The hierarchy component of every block subscribes to its parent's - the grid's - component container
    /// (<c>ComponentAdded</c>, <c>ComponentRemoved</c>): a grid of N blocks holds two events of N subscribers each.
    /// Taking one off copies the others, so a grid that closed paid N squared for it: 0.4 s for 64 production grids
    /// (dotTrace, refinery_perf cleanup), far more for one ship of ten thousand blocks. When the grid itself closes,
    /// its container goes with it and there is nothing to unsubscribe from: a block's hierarchy lets go of it. A
    /// block taken off a grid that lives on unsubscribes as before.
    /// </summary>
    [PatchShim]
    public static class HierarchyClose
    {
        private static FieldInfo _parentContainer;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("HierarchyClose", ctx, c =>
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var method = typeof(MyHierarchyComponentBase).GetMethod("OnBeforeRemovedFromContainer", any | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null)
                         ?? throw new MissingMethodException("MyHierarchyComponentBase.OnBeforeRemovedFromContainer");
            _parentContainer = typeof(MyHierarchyComponentBase).GetField("m_parentContainer", any)
                               ?? throw new MissingFieldException("MyHierarchyComponentBase.m_parentContainer");
            c.GetPattern(method).Prefixes.Add(typeof(HierarchyClose).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        });

        /// <summary>The parent closing: its container is let go of instead of unsubscribed from; the rest as vanilla.</summary>
        private static void Prefix(MyHierarchyComponentBase __instance)
        {
            if (_parentContainer.GetValue(__instance) is MyEntityComponentContainer container && container.Entity != null && container.Entity.MarkedForClose)
                _parentContainer.SetValue(__instance, null);
        }
    }
}
