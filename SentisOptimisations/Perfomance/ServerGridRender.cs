using System;
using System.Linq.Expressions;
using System.Reflection;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using VRageRender;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// A dedicated server does not draw, so it does not rebuild what a grid looks like.
    ///
    /// Every change to a grid - a block built, a weld step that moves a block to its next
    /// construction model, a grind, a hit - marks the grid's render cells dirty, and the grid then
    /// rebuilds them on the game thread: every cube part of every dirty cell, and the edge lines
    /// between the cubes, turned into instance data for a renderer the server does not have. On the
    /// welding bench that was a quarter of the whole welding cost and single frames of 18-20 ms.
    ///
    /// The cells are still kept up to date - parts are added and removed as before, off the game
    /// thread - only the final rebuild is skipped, and the dirty list is emptied so nothing asks
    /// for it again. Nothing on the server reads the instance data.
    /// </summary>
    [PatchShim]
    public static class ServerGridRender
    {
        private static Action<object> _clearDirtyCells;
        private static FieldInfo _dirtyCellsField;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("ServerGridRender", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            if (!Sandbox.Engine.Platform.Game.IsDedicated) return;

            _dirtyCellsField = typeof(MyCubeGridRenderData).GetField("m_dirtyCells",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var clear = _dirtyCellsField?.FieldType.GetMethod("Clear", Type.EmptyTypes);
            if (clear == null) throw new MissingMemberException("MyCubeGridRenderData.m_dirtyCells.Clear()");
            var set = Expression.Parameter(typeof(object));
            _clearDirtyCells = Expression.Lambda<Action<object>>(
                Expression.Call(Expression.Convert(set, _dirtyCellsField.FieldType), clear), set).Compile();

            var rebuild = typeof(MyCubeGridRenderData).GetMethod("RebuildDirtyCells",
                BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(RenderFlags) }, null);
            if (rebuild == null) throw new MissingMethodException("MyCubeGridRenderData.RebuildDirtyCells(RenderFlags)");
            ctx.GetPattern(rebuild).Prefixes.Add(typeof(ServerGridRender).GetMethod(nameof(RebuildDirtyCellsPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool RebuildDirtyCellsPrefix(MyCubeGridRenderData __instance)
        {
            try
            {
                var dirty = _dirtyCellsField.GetValue(__instance);
                if (dirty != null) _clearDirtyCells(dirty);
                return false;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "skipping a grid render rebuild failed");
                return true;
            }
        }
    }
}
