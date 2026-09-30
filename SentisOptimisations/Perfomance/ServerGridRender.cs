using System;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Entities.Blocks;
using System.Collections.Generic;
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
    ///
    /// Nor does a block added to the world rebuild its cell (<c>MyCubeGridRenderCell.RebuildInstanceParts</c>
    /// from the block's render component): a grid of 2700 blocks spawned whole rebuilt its cells block after
    /// block, ~30 ms of the frame it came in.
    ///
    /// Nor are the glowing parts of a block's subparts recoloured (<c>MyEntity.SetEmissivePartsForSubparts</c>: render
    /// messages for every subpart, recursively). A hydrogen engine sets its emissive state every frame through its
    /// capacity update: 0.4 s of 120 on the old server for colours nobody sees.
    ///
    /// Nor do text panels draw (<c>MyMultiTextPanelComponent.UpdateScreen</c>, every 10 frames per panel: whether the panel
    /// is within render distance, its textures, and the screen scripts run to make sprites). A screen script's sprites are
    /// thrown away on the server anyway - clients run their own (<c>MyTextPanelComponent.DispatchSprites</c> queues nothing
    /// while a script is selected). What the server does need stays: text written by mods is applied, and the sprites a
    /// programmable block drew are sent to the clients. 1.2-1.9 s of 120 on the old server.
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

            var cellRebuild = typeof(MyCubeGridRenderCell).GetMethod("RebuildInstanceParts",
                BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(RenderFlags) }, null);
            if (cellRebuild == null) throw new MissingMethodException("MyCubeGridRenderCell.RebuildInstanceParts(RenderFlags)");
            ctx.GetPattern(cellRebuild).Prefixes.Add(typeof(ServerGridRender).GetMethod(nameof(SkipPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
            SkipEmissive(ctx);
            SkipPanelDrawing(ctx);
        }

        private static bool SkipPrefix() => false;

        private static Func<MyMultiTextPanelComponent, List<MyTextPanelComponent>> _panels;
        private static Func<MyTextPanelComponent, bool> _spritesDirty;
        private static Action<MyTextPanelComponent> _sendSprites;

        private static void SkipPanelDrawing(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _panels = SentisOptimisationsPlugin.Accessors.Field<MyMultiTextPanelComponent, List<MyTextPanelComponent>>("m_panels");
            _spritesDirty = SentisOptimisationsPlugin.Accessors.Field<MyTextPanelComponent, bool>("m_areSpritesDirty");
            _sendSprites = (Action<MyTextPanelComponent>)Delegate.CreateDelegate(typeof(Action<MyTextPanelComponent>),
                typeof(MyTextPanelComponent).GetMethod("SendSpriteQueue", any, null, Type.EmptyTypes, null) ?? throw new MissingMethodException("MyTextPanelComponent", "SendSpriteQueue"));
            var update = typeof(MyMultiTextPanelComponent).GetMethod("UpdateScreen", any, null, new[] { typeof(bool) }, null)
                         ?? throw new MissingMethodException("MyMultiTextPanelComponent", "UpdateScreen");
            ctx.GetPattern(update).Prefixes.Add(typeof(ServerGridRender).GetMethod(nameof(PanelScreenPrefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>Only what a dedicated server needs of a panel's screen update.</summary>
        private static bool PanelScreenPrefix(MyMultiTextPanelComponent __instance)
        {
            try
            {
                var panels = _panels(__instance);
                if (panels == null) return false;
                for (var i = 0; i < panels.Count; i++)
                {
                    var panel = panels[i];
                    panel.UpdateModApiText();
                    if (_spritesDirty(panel)) _sendSprites(panel);
                }
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static void SkipEmissive(PatchContext ctx)
        {
            var subparts = typeof(VRage.Game.Entity.MyEntity).GetMethod("SetEmissivePartsForSubparts", BindingFlags.Instance | BindingFlags.Public)
                           ?? throw new MissingMethodException("MyEntity", "SetEmissivePartsForSubparts");
            ctx.GetPattern(subparts).Prefixes.Add(typeof(ServerGridRender).GetMethod(nameof(SkipPrefix), BindingFlags.Static | BindingFlags.NonPublic));
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
