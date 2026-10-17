using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SpaceEngineers.Game.SessionComponents;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace SentisOptimisations.Commands
{
    /// <summary>
    /// Admin commands for the grid storage of the Services Terminal (the vanilla garage): what is in it and the purge of
    /// records whose owner was removed from the world.
    ///
    /// The game keeps a record per stored grid in the world (owner, how it is shared) and the grid itself as files
    /// <c>gs-&lt;id&gt;*.sbc</c> in the save folder; it drops the records of a removed identity only when its own trash
    /// cleanup removes it. A player removed otherwise (an identity purge of a plugin) leaves their records behind:
    /// nobody may fetch them, yet every autosave copies their files. On the server (17.10.2026) 2469 of 3809 records
    /// were of 73 removed players.
    ///
    /// The records are read and changed on the game thread; the files are only looked at, on a worker. A purged record
    /// leaves its files, which the game deletes on the next load ("storage files are being deleted as this id is not
    /// tracked") - the way of the game's own cleanup.
    /// </summary>
    [Category("garage")]
    public class GarageCommands : CommandModule
    {
        private const int ListLimit = 50;
        private const int FindLimit = 30;
        private const int TopLimit = 10;
        private static readonly int[] InactiveDays = { 14, 30 };

        private static NLog.Logger Log => SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log;

        [Command("stats", "Grid storage totals: records, files, removed and inactive owners, the largest owners")]
        [Permission(MyPromoteLevel.Admin)]
        public void Stats()
        {
            OnGame(() =>
            {
                var storage = Storage();
                if (storage == null) return;
                var entries = Snapshot(storage);
                var owners = Owners(entries);
                var path = MySession.Static.CurrentPath;
                var known = new HashSet<Guid>(entries.Select(e => e.Id));
                Task.Run(() =>
                {
                    var files = CountFiles(path, known);
                    OnGame(() => Context.Respond(StatsText(entries, owners, files)));
                });
            });
        }

        [Command("list", "The grids of a player in the storage: !garage list <name | Steam id | identity id>")]
        [Permission(MyPromoteLevel.Admin)]
        public void List(string player = null)
        {
            var query = Query(player);
            if (query == null)
            {
                Context.Respond("Укажите игрока: !garage list <имя | Steam id | identity id>");
                return;
            }
            OnGame(() =>
            {
                var storage = Storage();
                if (storage == null) return;
                if (!ResolveOwner(query, out var ownerId, out var problem))
                {
                    Context.Respond(problem);
                    return;
                }
                var entries = Snapshot(storage).Where(e => e.Owner == ownerId).ToList();
                var title = $"Гараж {OwnerLabel(ownerId)}: {entries.Count} гридов, {entries.Sum(e => e.Pcu)} PCU";
                if (entries.Count == 0)
                {
                    Context.Respond(title);
                    return;
                }
                var path = MySession.Static.CurrentPath;
                Task.Run(() =>
                {
                    var dated = entries.Select(e => (Entry: e, At: StoredAt(path, e.Id)))
                        .OrderByDescending(x => x.At ?? DateTime.MinValue).ToList();
                    var text = new StringBuilder(title);
                    foreach (var (e, at) in dated.Take(ListLimit))
                        text.Append('\n').Append(Line(e, at));
                    if (dated.Count > ListLimit) text.Append($"\n... и ещё {dated.Count - ListLimit}");
                    OnGame(() => Context.Respond(text.ToString()));
                });
            });
        }

        [Command("find", "Stored grids whose name contains the text: !garage find <text>")]
        [Permission(MyPromoteLevel.Admin)]
        public void Find(string text = null)
        {
            var query = Query(text);
            if (query == null)
            {
                Context.Respond("Укажите часть названия: !garage find <текст>");
                return;
            }
            OnGame(() =>
            {
                var storage = Storage();
                if (storage == null) return;
                var found = Snapshot(storage).Where(e => GarageAudit.NameMatches(e.Name, query)).ToList();
                if (found.Count == 0)
                {
                    Context.Respond($"В гараже нет гридов с «{query}» в названии");
                    return;
                }
                var labels = found.Select(e => e.Owner).Distinct().ToDictionary(id => id, OwnerLabel);
                var path = MySession.Static.CurrentPath;
                Task.Run(() =>
                {
                    var dated = found.Select(e => (Entry: e, At: StoredAt(path, e.Id)))
                        .OrderByDescending(x => x.At ?? DateTime.MinValue).ToList();
                    var result = new StringBuilder($"Найдено гридов с «{query}»: {found.Count}");
                    foreach (var (e, at) in dated.Take(FindLimit))
                        result.Append('\n').Append(Line(e, at)).Append(" — ").Append(labels[e.Owner]);
                    if (dated.Count > FindLimit) result.Append($"\n... и ещё {dated.Count - FindLimit}");
                    OnGame(() => Context.Respond(result.ToString()));
                });
            });
        }

        [Command("purge deleted", "Records of owners removed from the world: shows them; '!garage purge deleted confirm' removes them")]
        [Permission(MyPromoteLevel.Admin)]
        public void PurgeDeleted(string confirm = null)
        {
            var apply = string.Equals(confirm, "confirm", StringComparison.OrdinalIgnoreCase);
            OnGame(() =>
            {
                var storage = Storage();
                if (storage == null) return;
                // classified afresh at the moment of removal: nothing older than this frame decides
                var entries = Snapshot(storage);
                var owners = Owners(entries);
                var dead = entries.Where(e => owners[e.Owner].State(e.Shared) == GarageOwnerState.Deleted).ToList();
                var busy = dead.Where(e => e.InTransition).ToList();
                var purge = dead.Where(e => !e.InTransition).ToList();
                var kept = entries.Count(e => owners[e.Owner].State(e.Shared) == GarageOwnerState.DeletedSharedWithFaction);

                var text = new StringBuilder();
                text.Append($"Записи удалённых игроков: {purge.Count} (PCU {purge.Sum(e => e.Pcu)}) у {purge.Select(e => e.Owner).Distinct().Count()} игроков");
                if (kept > 0) text.Append($"\nНе трогаются: {kept} записей, расшаренных на фракцию с живыми участниками");
                if (busy.Count > 0) text.Append($"\nНе трогаются: {busy.Count} записей в процессе выдачи");

                if (!apply)
                {
                    foreach (var group in purge.GroupBy(e => e.Owner).OrderByDescending(g => g.Count()).Take(TopLimit))
                        text.Append($"\n  {group.Key}: {group.Count()} гридов, {group.Sum(e => e.Pcu)} PCU (например, «{group.First().Name}»)");
                    if (purge.Count > 0) text.Append("\nУдалить: !garage purge deleted confirm");
                    Context.Respond(text.ToString());
                    return;
                }

                var removed = 0;
                foreach (var e in purge)
                {
                    if (!storage.DeleteGrid(e.Id)) continue;
                    removed++;
                    Log.Info($"Grid storage: purge: '{e.Name}' ({e.Id}) of removed owner {e.Owner} deleted ({e.Pcu} PCU, {e.Blocks} blocks)");
                }
                Log.Info($"Grid storage: purge by '{Context.Player?.DisplayName ?? "server"}': {removed} records of removed owners deleted; {kept} shared with a living faction and {busy.Count} being fetched kept");
                Context.Respond($"Удалено записей: {removed}. Файлы гридов игра удалит при следующей загрузке мира.\n{text}");
            });
        }

        /// <summary>A stored record as it is at this frame, with its details.</summary>
        private sealed class Entry
        {
            public Guid Id;
            public long Owner;
            public bool Shared;
            public bool InTransition;
            public string Name;
            public int Pcu;
            public int Blocks;
        }

        /// <summary>What is known of an owner of records.</summary>
        private sealed class Owner
        {
            public bool Exists;
            public bool FactionHasMember;
            public string Name;
            public int? DaysAway;

            public GarageOwnerState State(bool shared) => GarageAudit.OwnerState(Exists, shared, FactionHasMember);
        }

        private struct FileCount
        {
            public int Files;
            public long Bytes;
            public int Orphans;
            public bool Failed;
        }

        private void OnGame(Action work)
        {
            MyAPIGateway.Utilities.InvokeOnGameThread(() =>
            {
                try
                {
                    work();
                }
                catch (Exception e)
                {
                    Log.Error(e, "Grid storage: a garage command failed");
                    Context.Respond("Команда не выполнена: ошибка, подробности в логе");
                }
            });
        }

        private MyGridsStorageSessionComponent Storage()
        {
            var storage = MySession.Static?.GetComponent<MyGridsStorageSessionComponent>();
            if (storage == null) Context.Respond("Хранилище гридов недоступно");
            return storage;
        }

        private static List<Entry> Snapshot(MyGridsStorageSessionComponent storage) =>
            storage.GetStoredGridsData().Select(pair => new Entry
            {
                Id = pair.Key,
                Owner = pair.Value.OwnerId,
                Shared = pair.Value.OwnershipKind == Sandbox.Common.ObjectBuilders.MyStoredGridOwnershipKind.FactionShare,
                InTransition = pair.Value.InTransition,
                Name = pair.Value.SavedGridDetails?.DisplayName ?? "?",
                Pcu = pair.Value.SavedGridDetails?.PcuCount ?? 0,
                Blocks = pair.Value.SavedGridDetails?.BlockCount ?? 0
            }).ToList();

        private static Dictionary<long, Owner> Owners(List<Entry> entries)
        {
            var players = MySession.Static.Players;
            var online = new HashSet<long>(players.GetOnlinePlayers().Select(p => p.Identity.IdentityId));
            var now = DateTime.Now;
            var result = new Dictionary<long, Owner>();
            foreach (var ownerId in entries.Select(e => e.Owner).Distinct())
            {
                var identity = players.TryGetIdentity(ownerId);
                var faction = MySession.Static.Factions.GetPlayerFaction(ownerId);
                result[ownerId] = new Owner
                {
                    Exists = identity != null,
                    FactionHasMember = faction != null && faction.Members.Keys.Any(id => id != ownerId && players.HasIdentity(id)),
                    Name = identity?.DisplayName,
                    DaysAway = identity == null ? null
                        : online.Contains(ownerId) ? 0
                        : GarageAudit.DaysSince(Latest(identity.LastLoginTime, identity.LastLogoutTime), now)
                };
            }
            return result;
        }

        private static DateTime Latest(DateTime a, DateTime b) => a > b ? a : b;

        /// <summary>The gs- files of the save folder: how many, their size, and how many have no record.</summary>
        private static FileCount CountFiles(string path, HashSet<Guid> known)
        {
            var count = new FileCount();
            try
            {
                foreach (var file in new DirectoryInfo(path).EnumerateFiles("gs-*"))
                {
                    count.Files++;
                    count.Bytes += file.Length;
                    if (GarageAudit.TryParseFileId(file.Name, out var id) && !known.Contains(id)) count.Orphans++;
                }
            }
            catch (Exception e)
            {
                // a save moves the folder's files around meanwhile: the totals are then left out
                Log.Warn(e, "Grid storage: the storage files could not be counted");
                count.Failed = true;
            }
            return count;
        }

        /// <summary>When a grid was stored: its file was written then (null when the file is not there).</summary>
        private static DateTime? StoredAt(string path, Guid id)
        {
            try
            {
                var file = new FileInfo(Path.Combine(path, "gs-" + id + ".sbc"));
                return file.Exists ? file.LastWriteTime : (DateTime?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string StatsText(List<Entry> entries, Dictionary<long, Owner> owners, FileCount files)
        {
            var text = new StringBuilder();
            text.Append($"Гараж: {entries.Count} записей у {owners.Count} владельцев, {entries.Sum(e => e.Pcu)} PCU");
            text.Append(files.Failed
                ? "\nФайлы: не удалось посчитать (идёт сохранение?)"
                : $"\nФайлы gs-: {files.Files}, {GarageAudit.Size(files.Bytes)}; без записи: {files.Orphans} (игра удалит их при следующей загрузке)");

            var dead = entries.Where(e => owners[e.Owner].State(e.Shared) == GarageOwnerState.Deleted).ToList();
            var kept = entries.Count(e => owners[e.Owner].State(e.Shared) == GarageOwnerState.DeletedSharedWithFaction);
            text.Append($"\nУдалённые игроки: {dead.Count} записей, {dead.Sum(e => e.Pcu)} PCU у {dead.Select(e => e.Owner).Distinct().Count()} игроков");
            if (kept > 0) text.Append($" (ещё {kept} расшарены на живую фракцию)");

            foreach (var days in InactiveDays)
            {
                var away = entries.Where(e => owners[e.Owner].DaysAway >= days).ToList();
                text.Append($"\nНе заходили {days}+ дней: {away.Count} записей у {away.Select(e => e.Owner).Distinct().Count()} игроков");
            }

            text.Append($"\nБольше всего гридов (топ-{TopLimit}):");
            foreach (var group in entries.GroupBy(e => e.Owner).OrderByDescending(g => g.Count()).Take(TopLimit))
            {
                var owner = owners[group.Key];
                var away = owner.DaysAway.HasValue ? $", заходил {owner.DaysAway} дн. назад" : "";
                text.Append($"\n  {owner.Name ?? "удалён"} ({group.Key}): {group.Count()} гридов, {group.Sum(e => e.Pcu)} PCU{away}");
            }
            return text.ToString();
        }

        private static string Line(Entry e, DateTime? at)
        {
            var date = at?.ToString("dd.MM.yy HH:mm") ?? "нет файла";
            var marks = (e.Shared ? " [фракция]" : "") + (e.InTransition ? " [выдаётся]" : "");
            return $"{e.Name} — {e.Pcu} PCU, {e.Blocks} бл., {date}, {e.Id.ToString().Substring(0, 8)}{marks}";
        }

        private static string OwnerLabel(long ownerId)
        {
            var name = MySession.Static.Players.TryGetIdentity(ownerId)?.DisplayName;
            return name == null ? $"удалённого игрока {ownerId}" : $"{name} ({ownerId})";
        }

        /// <summary>The whole text after the command (names have spaces), without quotes; null when empty.</summary>
        private string Query(string first)
        {
            var raw = string.IsNullOrWhiteSpace(Context.RawArgs) ? first : Context.RawArgs;
            raw = raw?.Trim().Trim('"').Trim();
            return string.IsNullOrEmpty(raw) ? null : raw;
        }

        /// <summary>
        /// The owner meant: a Steam id or an identity id (also of a removed player, who has records but no name), else
        /// a name - an exact one first, else the only one containing the text.
        /// </summary>
        private static bool ResolveOwner(string query, out long ownerId, out string problem)
        {
            var players = MySession.Static.Players;
            problem = null;
            if (ulong.TryParse(query, out var steamId) && GarageAudit.IsSteamId(steamId))
            {
                ownerId = players.TryGetIdentityId(steamId);
                if (ownerId != 0) return true;
                problem = $"Игрок со Steam id {steamId} не найден";
                return false;
            }
            if (long.TryParse(query, out ownerId)) return true;

            var identities = players.GetAllIdentities().Where(i => i.DisplayName != null).ToList();
            var exact = identities.Where(i => string.Equals(i.DisplayName, query, StringComparison.OrdinalIgnoreCase)).ToList();
            var matches = exact.Count > 0 ? exact : identities.Where(i => GarageAudit.NameMatches(i.DisplayName, query)).ToList();
            if (matches.Count == 1)
            {
                ownerId = matches[0].IdentityId;
                return true;
            }
            ownerId = 0;
            problem = matches.Count == 0
                ? $"Игрок «{query}» не найден"
                : $"Под «{query}» подходят несколько игроков, уточните (можно identity id):\n" +
                  string.Join("\n", matches.Take(TopLimit).Select(i => $"  {i.DisplayName} ({i.IdentityId})"));
            return false;
        }
    }
}
