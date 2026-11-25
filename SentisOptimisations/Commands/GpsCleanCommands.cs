using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisOptimisations.Commands
{
    /// <summary>
    /// The admins' clean-up of GPS marks nobody needs any more: <c>!gps clean [signals] [linked] [name &lt;text&gt;] [apply]</c>.
    /// Without <c>apply</c> only shows what would go. Always:
    /// <list type="bullet">
    /// <item>the marks of an identity that is no longer in the world;</item>
    /// <item>a global encounter's signal ("Unidentified Signal") whose encounter is gone;</item>
    /// <item>a contract's mark (its drop-off, its salvage site) whose contract is no longer active;</item>
    /// <item>a second mark of the same name at the same place (within a metre) of one player: one is kept.</item>
    /// </list>
    /// Asked for: <c>signals</c> every encounter signal, live or not; <c>linked</c> a mark that follows an entity which is gone
    /// (not by default: a ship in the garage is out of the world and comes back); <c>name</c> every mark of that name (case
    /// not minded; the rest of the line). A player's own marks (no entity, no contract) are never touched otherwise. A removed
    /// mark goes from the player's game too (the game's delete, sent to him when online). Every run that removes is logged.
    /// </summary>
    public class GpsCleanCommands : CommandModule
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private const string Usage = "!gps clean [signals] [linked] [name <текст>] [apply] - убрать ненужные GPS-метки: метки удалённых личностей, " +
                                     "сигналы исчезнувших встреч, метки завершённых контрактов, дубли (то же имя в том же месте у одного игрока). " +
                                     "signals: все сигналы встреч; linked: метки, привязанные к исчезнувшей сущности; name: все метки с этим именем. " +
                                     "Без apply только показывает";

        private static readonly FieldInfo PlayerGpss =
            typeof(MyGpsCollection).GetField("m_playerGpss", BindingFlags.NonPublic | BindingFlags.Instance);

        public enum Reason
        {
            Keep,
            OwnerGone,
            SignalGone,
            ContractOver,
            Duplicate,
            Signal,
            LinkedGone,
            Named,
        }

        public static string Text(Reason r)
        {
            switch (r)
            {
                case Reason.OwnerGone: return "личности нет в мире";
                case Reason.SignalGone: return "сигнал исчезнувшей встречи";
                case Reason.ContractOver: return "контракт не активен";
                case Reason.Duplicate: return "дубль";
                case Reason.Signal: return "сигнал встречи (signals)";
                case Reason.LinkedGone: return "сущности нет (linked)";
                case Reason.Named: return "по имени (name)";
                default: return "оставить";
            }
        }

        public sealed class Options
        {
            public bool Apply;
            public bool Signals;
            public bool Linked;
            public string Name;
        }

        /// <summary>What a mark is, as the judgement needs it.</summary>
        public struct Facts
        {
            public bool OwnerExists;
            public string Name;
            public bool Encounter;
            public long EntityId;
            public bool EntityAlive;
            public long ContractId;
            /// <summary>Whether contracts can be told at all (the contract system is there).</summary>
            public bool ContractsKnown;
            public bool ContractActive;
            public bool Duplicate;
        }

        /// <summary>Why a mark goes, or Keep.</summary>
        public static Reason Judge(Facts f, Options o)
        {
            if (!f.OwnerExists) return Reason.OwnerGone;
            if (f.Encounter && f.EntityId != 0 && !f.EntityAlive) return Reason.SignalGone;
            if (f.ContractId != 0 && f.ContractsKnown && !f.ContractActive) return Reason.ContractOver;
            if (f.Duplicate) return Reason.Duplicate;
            if (o.Signals && f.Encounter) return Reason.Signal;
            if (o.Linked && !f.Encounter && f.EntityId != 0 && !f.EntityAlive) return Reason.LinkedGone;
            if (!string.IsNullOrEmpty(o.Name) && string.Equals((f.Name ?? "").Trim(), o.Name.Trim(), StringComparison.OrdinalIgnoreCase)) return Reason.Named;
            return Reason.Keep;
        }

        public static Options Parse(IList<string> args, out string error)
        {
            error = null;
            var o = new Options();
            var i = 0;
            if (args != null && args.Count > 0 && args[0].Equals("clean", StringComparison.OrdinalIgnoreCase)) i = 1;
            for (; args != null && i < args.Count; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "apply": o.Apply = true; break;
                    case "signals": o.Signals = true; break;
                    case "linked": o.Linked = true; break;
                    case "name":
                        if (i + 1 >= args.Count)
                        {
                            error = "name <имя метки>";
                            return null;
                        }
                        // the rest of the line, but a trailing apply is the switch
                        var rest = args.Skip(i + 1).ToList();
                        if (rest.Count > 1 && rest[rest.Count - 1].Equals("apply", StringComparison.OrdinalIgnoreCase))
                        {
                            o.Apply = true;
                            rest.RemoveAt(rest.Count - 1);
                        }
                        o.Name = string.Join(" ", rest);
                        i = args.Count;
                        break;
                    default:
                        error = "Непонятно: " + args[i];
                        return null;
                }
            }
            return o;
        }

        [Command("gps clean", Usage)]
        [Permission(MyPromoteLevel.Admin)]
        public void Clean()
        {
            var options = Parse(Context.Args, out var error);
            if (options == null)
            {
                Context.Respond(error + "\n" + Usage);
                return;
            }
            Context.Respond(Run(options, Context.Player?.DisplayName ?? "console"));
        }

        internal static string Run(Options o, string who)
        {
            var all = PlayerGpss?.GetValue(MySession.Static.Gpss) as Dictionary<long, Dictionary<int, MyGps>>;
            if (all == null) return "Список GPS игры не найден (другая версия игры?)";
            var contracts = MySession.Static.GetComponent<MySessionComponentContractSystem>();
            var active = contracts == null ? null : new HashSet<long>(contracts.ActiveContracts.Select(c => c.Id));
            var counts = new Dictionary<Reason, int>();
            var names = new Dictionary<string, int>();
            var goneOwners = new List<long>();
            var deletes = new List<KeyValuePair<long, int>>();
            var total = 0;
            foreach (var pair in all)
            {
                var ownerExists = MySession.Static.Players.TryGetIdentity(pair.Key) != null;
                var seen = new List<KeyValuePair<string, Vector3D>>();
                foreach (var gps in pair.Value.Values.OrderBy(g => g.Hash))
                {
                    total++;
                    var at = gps.Coords;
                    var name = gps.Name ?? "";
                    var duplicate = seen.Any(s => s.Key == name && Vector3D.DistanceSquared(s.Value, at) < 1);
                    seen.Add(new KeyValuePair<string, Vector3D>(name, at));
                    var facts = new Facts
                    {
                        OwnerExists = ownerExists,
                        Name = name,
                        Encounter = gps.IsGlobalEncounterGPS,
                        EntityId = gps.EntityId,
                        EntityAlive = gps.EntityId != 0 && MyEntities.EntityExists(gps.EntityId),
                        ContractId = gps.ContractId,
                        ContractsKnown = active != null,
                        ContractActive = active != null && active.Contains(gps.ContractId),
                        Duplicate = duplicate,
                    };
                    var reason = Judge(facts, o);
                    if (reason == Reason.Keep) continue;
                    counts[reason] = counts.TryGetValue(reason, out var c) ? c + 1 : 1;
                    names[name] = names.TryGetValue(name, out var n) ? n + 1 : 1;
                    if (reason != Reason.OwnerGone) deletes.Add(new KeyValuePair<long, int>(pair.Key, gps.Hash));
                }
                if (!ownerExists) goneOwners.Add(pair.Key);
            }
            var gone = counts.Values.Sum();
            var text = new StringBuilder();
            text.Append(o.Apply ? "Удалено" : "Будет удалено (добавьте apply)").Append($" {gone} из {total} GPS-меток");
            if (gone > 0)
            {
                text.Append(": ").Append(string.Join(", ", counts.OrderByDescending(p => p.Value).Select(p => $"{Text(p.Key)} {p.Value}")));
                text.Append(". Чаще всего: ").Append(string.Join(", ", names.OrderByDescending(p => p.Value).Take(8).Select(p => $"«{p.Key}» {p.Value}")));
            }
            if (!o.Apply || gone == 0) return text.ToString();

            foreach (var d in deletes)
            {
                try
                {
                    MySession.Static.Gpss.SendDeleteGpsRequest(d.Key, d.Value);
                }
                catch (Exception e)
                {
                    Log.Error(e, $"gps clean: mark {d.Value} of {d.Key} not removed");
                }
            }
            foreach (var id in goneOwners) all.Remove(id);
            Log.Info($"gps clean by {who}: {text}");
            return text.ToString();
        }
    }
}
