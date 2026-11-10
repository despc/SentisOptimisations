using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using NLog;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisOptimisations.Commands
{
    /// <summary>
    /// The admins' clean-up of grids round them: <c>!gridclean &lt;radius m&gt; [noblock &lt;type,type,...&gt;]
    /// [noplayers &lt;m&gt;] [static|dynamic] [nopower] [lessblocks &lt;N&gt;] [at x y z] [delete]</c>. Without <c>delete</c> only shows what would go; with it removes that.
    ///
    /// A group of grids joined by rotors, pistons and hinges is judged and removed as one (a rotor's head has no cockpit, its
    /// base may): it goes when any of its grids is within the radius and every condition given holds for the group -
    /// <c>noblock</c> not one block of the types listed on any of its grids (a type is a block's type id without
    /// MyObjectBuilder_ - Cockpit, Beacon, RemoteControl - or its subtype, or Type/Subtype; a name no block of the game has
    /// is refused, lest a typo leave nothing kept); <c>noplayers N</c> no player's character (not a bot's, not the admin's
    /// own) within N m of any of its grids; <c>static</c> only stations (a group with a static grid: a station's rotor head
    /// is of the station), <c>dynamic</c> only ships (every grid of the group not static); neither: both; <c>nopower</c> no
    /// working source of electricity on any of its grids (a block on and whole that has power to give: a charged battery not
    /// set to recharge, a reactor with uranium, a solar panel in the sun, a fuelled engine...); <c>lessblocks N</c> fewer than
    /// N blocks on all its grids together. A group someone sits in or controls is never removed, nor projections. Every
    /// removal is logged.
    /// </summary>
    public class GridCleanCommands : CommandModule
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private const int Shown = 15;

        private const string Usage = "!gridclean <радиус м> [noblock <тип,тип,...>] [noplayers <м>] [static|dynamic] [nopower] [lessblocks <N>] [at x y z] [delete] - " +
                                     "noblock: удалять только гриды без единого блока этих типов (Cockpit,Beacon,RemoteControl или сабтип); " +
                                     "static/dynamic: только станции или только корабли (по умолчанию все); nopower: без работающего источника энергии; " +
                                     "lessblocks N: меньше N блоков на всей группе; без delete только показывает, что будет удалено";

        private static readonly string[] Words = { "noblock", "noplayers", "static", "dynamic", "nopower", "lessblocks", "at", "delete" };

        internal sealed class Filter
        {
            public double Radius;
            public readonly List<string> NoBlock = new List<string>();
            public double NoPlayersM;   // 0: not asked
            public bool? Static;   // null: both
            public bool NoPower;
            public int LessBlocks;   // 0: not asked
            public Vector3D? At;
            public bool Delete;

            public override string ToString() =>
                (Static == true ? "только статические, " : Static == false ? "только динамические, " : "") +
                (NoPower ? "без энергии, " : "") + (LessBlocks > 0 ? $"меньше {LessBlocks} блоков, " : "") +
                (NoBlock.Count > 0 ? "без блоков " + string.Join(",", NoBlock) + ", " : "") +
                (NoPlayersM > 0 ? $"нет игроков ближе {PcuCommands.N(NoPlayersM)} м, " : "") + $"в радиусе {PcuCommands.N(Radius)} м";
        }

        [Command("gridclean", Usage)]
        [Permission(MyPromoteLevel.Admin)]
        public void GridClean()
        {
            var filter = Parse(Context.Args, out var error);
            if (filter == null)
            {
                Context.Respond(error + "\n" + Usage);
                return;
            }
            Vector3D center;
            var admin = Context.Player?.Character?.PositionComp.GetPosition() ?? Context.Player?.Controller?.ControlledEntity?.Entity?.PositionComp.GetPosition();
            if (filter.At.HasValue) center = filter.At.Value;
            else if (admin.HasValue) center = admin.Value;
            else
            {
                Context.Respond("Нет позиции: зайдите персонажем или задайте точку: at <x> <y> <z>");
                return;
            }
            Context.Respond(Run(center, filter, Context.Player?.IdentityId ?? 0, Context.Player?.DisplayName ?? "console"));
        }

        internal static Filter Parse(IList<string> args, out string error)
        {
            error = null;
            var filter = new Filter();
            if (args == null || args.Count == 0 || !Number(args[0], out filter.Radius) || filter.Radius <= 0)
            {
                error = "Радиус должен быть больше нуля.";
                return null;
            }
            for (var i = 1; i < args.Count; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "delete": filter.Delete = true; break;
                    case "nopower": filter.NoPower = true; break;
                    case "lessblocks":
                        if (i + 1 >= args.Count || !int.TryParse(args[++i], out filter.LessBlocks) || filter.LessBlocks <= 0)
                        {
                            error = "lessblocks <число блоков больше нуля>";
                            return null;
                        }
                        break;
                    case "static":
                    case "dynamic":
                        var wanted = args[i].ToLowerInvariant() == "static";
                        if (filter.Static.HasValue && filter.Static != wanted)
                        {
                            error = "static и dynamic вместе - это все гриды, не указывайте ни того, ни другого.";
                            return null;
                        }
                        filter.Static = wanted;
                        break;
                    case "noblock":
                        // "Cockpit,Beacon" or "Cockpit, Beacon" (split at the space): up to the next word of the command
                        var before = filter.NoBlock.Count;
                        while (i + 1 < args.Count && !Words.Contains(args[i + 1].ToLowerInvariant()))
                            filter.NoBlock.AddRange(args[++i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t.Length > 0));
                        if (filter.NoBlock.Count == before)
                        {
                            error = "noblock <тип,тип,...>, например noblock Cockpit,Beacon";
                            return null;
                        }
                        break;
                    case "noplayers":
                        if (i + 1 >= args.Count || !Number(args[++i], out filter.NoPlayersM) || filter.NoPlayersM <= 0)
                        {
                            error = "noplayers <метров больше нуля>";
                            return null;
                        }
                        break;
                    case "at":
                        if (i + 3 >= args.Count || !Number(args[i + 1], out var x) || !Number(args[i + 2], out var y) || !Number(args[i + 3], out var z))
                        {
                            error = "at <x> <y> <z>";
                            return null;
                        }
                        filter.At = new Vector3D(x, y, z);
                        i += 3;
                        break;
                    default:
                        error = "Не понял условие \"" + args[i] + "\".";
                        return null;
                }
            }
            var unknown = filter.NoBlock.Where(t => !Known(t)).ToList();
            if (unknown.Count > 0)
            {
                error = "Нет таких типов блоков: " + string.Join(", ", unknown) + " (тип без MyObjectBuilder_, сабтип или Тип/Сабтип).";
                return null;
            }
            return filter;
        }

        private static bool Number(string text, out double value) =>
            double.TryParse(text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>A block of this definition is of the type named: its type id (without MyObjectBuilder_), its subtype, or Type/Subtype.</summary>
        private static bool Is(MyDefinitionId id, string name)
        {
            var type = id.TypeId.ToString().Replace("MyObjectBuilder_", "");
            return name.Equals(type, StringComparison.OrdinalIgnoreCase) || name.Equals(id.SubtypeName, StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(type + "/" + id.SubtypeName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Known(string name) =>
            MyDefinitionManager.Static.GetAllDefinitions().OfType<MyCubeBlockDefinition>().Any(d => Is(d.Id, name));

        private static bool HasAny(MyCubeGrid grid, List<string> types) =>
            grid.GetBlocks().Any(b => types.Any(t => Is(b.BlockDefinition.Id, t)));

        /// <summary>The groups the filter picks round the point, removed when it says delete; the answer (game thread).</summary>
        internal static string Run(Vector3D center, Filter filter, long adminIdentity, string adminName)
        {
            var sphere = new BoundingSphereD(center, filter.Radius);
            var players = filter.NoPlayersM > 0 ? Players(adminIdentity) : new List<Vector3D>();
            var seen = new HashSet<MyCubeGrid>();
            var picked = new List<List<MyCubeGrid>>();
            var kept = 0;
            foreach (var entity in MyEntities.GetEntities().ToList())
            {
                if (!(entity is MyCubeGrid grid) || grid.IsPreview || grid.Closed || grid.MarkedForClose || seen.Contains(grid)) continue;
                var group = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid).Where(g => !g.Closed && !g.MarkedForClose && !g.IsPreview).ToList();
                if (group.Count == 0) group.Add(grid);
                foreach (var g in group) seen.Add(g);
                if (group.All(g => g.PositionComp.WorldAABB.Contains(sphere) == ContainmentType.Disjoint)) continue;
                if (filter.Static.HasValue && group.Any(g => g.IsStatic) != filter.Static.Value) continue;
                if (filter.LessBlocks > 0 && group.Sum(g => g.BlocksCount) >= filter.LessBlocks) continue;
                if (Occupied(group)) { kept++; continue; }
                if (filter.NoBlock.Count > 0 && group.Any(g => HasAny(g, filter.NoBlock))) continue;
                if (filter.NoPower && group.Any(Powered)) continue;
                if (players.Count > 0 && group.Any(g => players.Any(p => g.PositionComp.WorldAABB.Distance(p) < filter.NoPlayersM))) continue;
                picked.Add(group);
            }

            var pcu = picked.Sum(group => group.Sum(g => (long)g.BlocksPCU));
            var grids = picked.Sum(group => group.Count);
            var text = new StringBuilder();
            text.Append((filter.Delete ? "Удалено" : "Будет удалено") + $": {picked.Count} групп, {grids} гридов, {PcuCommands.N(pcu)} PCU " +
                        $"({filter}, от {center.X:0} {center.Y:0} {center.Z:0})");
            if (kept > 0) text.Append($"; не тронуто {kept} групп, где кто-то сидит или управляет");
            foreach (var group in picked.OrderByDescending(group => group.Sum(g => g.BlocksPCU)).Take(Shown))
            {
                var main = group.OrderByDescending(g => g.BlocksCount).First();
                text.Append($"\n  {main.DisplayName}{(group.Count > 1 ? $" (+{group.Count - 1})" : "")} - {PcuCommands.N(group.Sum(g => g.BlocksPCU))} PCU, " +
                            $"{PcuCommands.Name(main.BigOwners.Count > 0 ? main.BigOwners[0] : 0L)}, {PcuCommands.N(main.PositionComp.WorldAABB.Distance(center))} м");
            }
            if (picked.Count > Shown) text.Append($"\n  ... и ещё {picked.Count - Shown}");
            if (!filter.Delete)
            {
                if (picked.Count > 0) text.Append("\nЧтобы удалить, повторите команду с delete.");
                return text.ToString();
            }

            foreach (var group in picked)
            foreach (var grid in group)
            {
                if (grid.Closed || grid.MarkedForClose) continue;
                Log.Warn($"!gridclean by {adminName}: removed \"{grid.DisplayName}\" {grid.EntityId}, {grid.BlocksPCU} PCU, owner " +
                         $"{PcuCommands.Name(grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0L)}, at {grid.PositionComp.GetPosition()} ({filter})");
                grid.Close();
            }
            return text.ToString();
        }

        /// <summary>A block of the grid, on and whole, can give electricity now (charge, fuel, sun, wind).</summary>
        private static bool Powered(MyCubeGrid grid)
        {
            foreach (var block in grid.GetFatBlocks())
            {
                if (!(block is MyFunctionalBlock functional) || !functional.IsWorking) continue;
                var source = block.Components.Get<MyResourceSourceComponent>();
                if (source == null || !source.ResourceTypes.Contains(Electricity)) continue;
                if (source.ProductionEnabledByType(Electricity) && source.HasCapacityRemainingByType(Electricity) && source.MaxOutputByType(Electricity) > 0) return true;
            }
            return false;
        }

        private static readonly MyDefinitionId Electricity = MyResourceDistributorComponent.ElectricityId;

        /// <summary>Someone sits in a cockpit of the group, or controls one of its grids (remote control, turret).</summary>
        private static bool Occupied(List<MyCubeGrid> group)
        {
            if (group.Any(g => g.GetFatBlocks().Any(b => b is MyShipController c && c.Pilot != null))) return true;
            foreach (var player in MySession.Static.Players.GetOnlinePlayers())
            {
                var controlled = player.Controller?.ControlledEntity?.Entity;
                if (controlled is MyCubeBlock block && group.Contains(block.CubeGrid)) return true;
                if (controlled is MyCubeGrid grid && group.Contains(grid)) return true;
            }
            return false;
        }

        /// <summary>Where the online players' characters are: not the bots', not the admin's own.</summary>
        private static List<Vector3D> Players(long adminIdentity)
        {
            var at = new List<Vector3D>();
            foreach (var player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player.Identity.IdentityId == adminIdentity || IsBot(player.Id.SteamId)) continue;
                var entity = player.Controller?.ControlledEntity?.Entity ?? player.Character;
                if (entity != null) at.Add(entity.PositionComp.GetPosition());
            }
            return at;
        }

        private static MethodInfo _isBot;
        private static bool _isBotLooked;

        /// <summary>SentisAi's bots are network clients too; they keep no grid.</summary>
        private static bool IsBot(ulong steamId)
        {
            if (!_isBotLooked)
            {
                _isBotLooked = true;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    _isBot = _isBot ?? assembly.GetType("SentisAi.Net.BotConnections", false)?.GetMethod("IsBot", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ulong) }, null);
            }
            try { return _isBot != null && (bool)_isBot.Invoke(null, new object[] { steamId }); }
            catch (Exception) { return false; }
        }
    }
}
