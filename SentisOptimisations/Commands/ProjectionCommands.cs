using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SentisOptimisationsPlugin.Perfomance;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game;
using VRage.Game.ModAPI;

namespace SentisOptimisations.Commands
{
    /// <summary>
    /// <c>!projections nested [apply]</c>: the blueprints of projectors inside the blueprints the world's projectors hold (a
    /// projection in a projection), counted, and with <c>apply</c> taken out - the projectors' own blueprints stay. What
    /// NestedProjections does to every new blueprint and at every start, for the projectors loaded now. The projection on
    /// the screen is not rebuilt: the save is cleaned at once, the projection's copy at its next reload.
    /// </summary>
    public class ProjectionCommands : CommandModule
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private const string Usage = "!projections nested [apply] - убрать проекции из проекций: чертежи проекторов внутри чертежей, загруженных в проекторы (сам чертёж проектора остаётся). Без apply только показывает";

        private const BindingFlags Field = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo Original = typeof(MyProjectorBase).GetField("m_originalGridBuilders", Field);
        private static readonly FieldInfo Saved = typeof(MyProjectorBase).GetField("m_savedProjections", Field);

        [Command("projections nested", Usage)]
        [Permission(MyPromoteLevel.Admin)]
        public void Nested()
        {
            var apply = Context.Args.Any(a => a.Equals("apply", System.StringComparison.OrdinalIgnoreCase));
            if (Context.Args.Any(a => !a.Equals("apply", System.StringComparison.OrdinalIgnoreCase)))
            {
                Context.Respond(Usage);
                return;
            }
            Context.Respond(Run(apply, Context.Player?.DisplayName ?? "console"));
        }

        /// <summary>The blueprints of the projectors in these grids: how many.</summary>
        private static int Count(IEnumerable<MyObjectBuilder_CubeGrid> grids) =>
            grids?.Where(g => g?.CubeBlocks != null).SelectMany(g => g.CubeBlocks).OfType<MyObjectBuilder_ProjectorBase>()
                .Sum(p => (p.ProjectedGrids?.Count ?? 0) + (p.ProjectedGrid != null ? 1 : 0)) ?? 0;

        internal static string Run(bool apply, string who)
        {
            if (Original == null || Saved == null) return "Поля проектора игры не найдены (другая версия игры?)";
            int projectors = 0, blueprints = 0;
            var top = new List<KeyValuePair<string, int>>();
            foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
            {
                if (grid.MarkedForClose || grid.IsPreview) continue;
                foreach (var projector in grid.GetFatBlocks<MyProjectorBase>())
                {
                    var lists = new[] { Original.GetValue(projector) as List<MyObjectBuilder_CubeGrid>, Saved.GetValue(projector) as List<MyObjectBuilder_CubeGrid> }
                        .Where(l => l != null).Distinct().ToList();
                    var n = lists.Sum(Count);
                    if (n == 0) continue;
                    projectors++;
                    blueprints += n;
                    top.Add(new KeyValuePair<string, int>($"{grid.DisplayName} / {projector.CustomName}", n));
                    if (apply) foreach (var list in lists) NestedProjections.Strip(list);
                }
            }
            var text = (apply ? "Убрано" : "Будет убрано (добавьте apply)") + $" {blueprints} вложенных чертежей из {projectors} проекторов";
            if (top.Count > 0) text += ": " + string.Join(", ", top.OrderByDescending(p => p.Value).Take(10).Select(p => $"{p.Key} ({p.Value})"));
            if (apply && blueprints > 0) Log.Info($"projections nested by {who}: {text}");
            return text;
        }
    }
}
