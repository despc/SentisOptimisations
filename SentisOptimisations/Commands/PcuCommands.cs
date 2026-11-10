using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisOptimisations.Commands
{
    /// <summary>
    /// The PCU of the grids round a point, for admins: <c>!pcu &lt;radius m&gt; [x y z]</c> - round the admin's character,
    /// or the point given (from the console). Every grid of the world is counted, loaded on the admin's client or not
    /// (projections are not): the total, the grids with the most PCU, the owners with the most.
    /// </summary>
    public class PcuCommands : CommandModule
    {
        private const int Top = 10;

        [Command("pcu", "PCU of the grids within a radius: !pcu <radius m> [x y z] (round your character, or the point)")]
        [Permission(MyPromoteLevel.Admin)]
        public void Pcu(double radius = 5000, double x = double.NaN, double y = double.NaN, double z = double.NaN)
        {
            if (radius <= 0 || double.IsNaN(radius) || double.IsInfinity(radius))
            {
                Context.Respond("Радиус должен быть больше нуля: !pcu <радиус в м> [x y z]");
                return;
            }
            Vector3D center;
            if (!double.IsNaN(x) && !double.IsNaN(y) && !double.IsNaN(z)) center = new Vector3D(x, y, z);
            else if (Context.Player?.Character != null) center = Context.Player.Character.PositionComp.GetPosition();
            else if (Context.Player?.Controller?.ControlledEntity?.Entity != null) center = Context.Player.Controller.ControlledEntity.Entity.PositionComp.GetPosition();
            else
            {
                Context.Respond("Нет позиции: зайдите персонажем или задайте точку: !pcu <радиус> <x> <y> <z>");
                return;
            }
            Context.Respond(Report(center, radius));
        }

        /// <summary>The report of the grids within the radius of the point (game thread).</summary>
        internal static string Report(Vector3D center, double radius)
        {
            var sphere = new BoundingSphereD(center, radius);
            var grids = new List<(MyCubeGrid Grid, double Distance)>();
            foreach (var entity in MyEntities.GetEntities())
            {
                if (!(entity is MyCubeGrid grid) || grid.IsPreview || grid.Closed || grid.MarkedForClose) continue;
                var box = grid.PositionComp.WorldAABB;
                if (box.Contains(sphere) == ContainmentType.Disjoint) continue;
                grids.Add((grid, box.Distance(center)));
            }
            var total = grids.Sum(g => (long)g.Grid.BlocksPCU);
            var blocks = grids.Sum(g => (long)g.Grid.BlocksCount);
            var text = new StringBuilder();
            text.Append($"PCU в радиусе {N(radius)} м от {center.X:0} {center.Y:0} {center.Z:0}: {N(total)} PCU, {grids.Count} гридов, {N(blocks)} блоков");
            if (grids.Count == 0) return text.ToString();

            text.Append($"\nБольше всего PCU (из {grids.Count}):");
            foreach (var (grid, distance) in grids.OrderByDescending(g => g.Grid.BlocksPCU).Take(Top))
                text.Append($"\n  {N(grid.BlocksPCU)} PCU - {grid.DisplayName} ({Owner(grid)}), {N(distance)} м, {N(grid.BlocksCount)} блоков");

            var owners = grids.GroupBy(g => g.Grid.BigOwners.Count > 0 ? g.Grid.BigOwners[0] : 0L)
                .Select(o => (Owner: o.Key, Pcu: o.Sum(g => (long)g.Grid.BlocksPCU), Grids: o.Count()))
                .OrderByDescending(o => o.Pcu).ToList();
            text.Append($"\nПо владельцам (из {owners.Count}):");
            foreach (var owner in owners.Take(Top))
                text.Append($"\n  {N(owner.Pcu)} PCU - {Name(owner.Owner)}, {owner.Grids} гридов");
            return text.ToString();
        }

        private static string Owner(MyCubeGrid grid) => Name(grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0L);

        internal static string Name(long identityId)
        {
            if (identityId == 0) return "без владельца";
            var identity = MySession.Static.Players.TryGetIdentity(identityId);
            var faction = MySession.Static.Factions.TryGetPlayerFaction(identityId);
            return (identity?.DisplayName ?? identityId.ToString()) + (faction != null ? " [" + faction.Tag + "]" : "");
        }

        private static readonly NumberFormatInfo Spaces = new NumberFormatInfo { NumberGroupSeparator = " " };

        internal static string N(double value) => value.ToString("#,0", Spaces);
    }
}
