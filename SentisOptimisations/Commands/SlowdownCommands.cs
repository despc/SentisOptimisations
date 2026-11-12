using System;
using SentisOptimisations.Slowdowns;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace SentisOptimisations.Commands
{
    /// <summary>
    /// The slowdowns at the peak load, for admins: <c>!slowdowns</c> - whether they are on, the simulation speed, what
    /// each held back since the start; <c>!slowdowns auto|always|off</c> - at the peak only, always, never (the settings'
    /// "Slowdowns at the peak load" and "Slowdowns always on", saved); <c>!slowdowns &lt;name&gt; on|off</c> - one of them.
    /// </summary>
    public class SlowdownCommands : CommandModule
    {
        [Command("slowdowns", "The slowdowns at the peak load: !slowdowns [auto|always|off], !slowdowns <name> <on|off>")]
        [Permission(MyPromoteLevel.Admin)]
        public void Slowdowns(string mode = "", string state = "")
        {
            var config = SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config;
            if (config == null)
            {
                Context.Respond("Нет настроек SO");
                return;
            }
            switch (mode.Trim().ToLowerInvariant())
            {
                case "":
                    break;
                case "auto":
                    config.SlowdownsEnabled = true;
                    config.SlowdownsAlways = false;
                    break;
                case "always":
                    config.SlowdownsEnabled = true;
                    config.SlowdownsAlways = true;
                    break;
                case "off":
                    config.SlowdownsEnabled = false;
                    config.SlowdownsAlways = false;
                    break;
                default:
                    if (!Toggle(config, mode.Trim().ToLowerInvariant(), state.Trim().ToLowerInvariant()))
                    {
                        Context.Respond("!slowdowns [auto|always|off] - auto: по нагрузке сервера, always: всегда, off: никогда; " +
                                        "!slowdowns <scripts|turretsearch|idleturrets|welders|sync> <on|off> - одно замедление");
                        return;
                    }
                    break;
            }
            if (mode.Length > 0)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.SaveConfig();
                // applied at once, not at the next second
                PeakLoad.Second(double.IsNaN(PeakLoad.LastLoad) ? 0 : PeakLoad.LastLoad, double.IsNaN(PeakLoad.LastSpeed) ? 1 : PeakLoad.LastSpeed, PeakLoad.Settings.FromPlugin());
            }
            Context.Respond(Describe(config));
        }

        internal static string Describe(SentisOptimisationsPlugin.MainConfig config)
        {
            var mode = !config.SlowdownsEnabled ? "off" : config.SlowdownsAlways ? "always" : "auto";
            return $"Нагрузка сервера {PeakLoad.LastLoad:F0}% (скорость симуляции {PeakLoad.LastSpeed:P0}), режим {mode}\n" +
                   $"Замедления: {(PeakLoad.Active ? "ВКЛ" : "выкл")} (включаются при нагрузке > {config.SlowdownsLoadPercent}%, выключаются при < {config.SlowdownsOffLoadPercent}%); включались {PeakLoad.Peaks} раз, секунд включены {PeakLoad.PeakSeconds}\n" +
                   $"С начала: запусков скриптов пропущено {ScriptSlowdown.Skipped} ({On(config.SlowScripts)}), кругов поиска турелей {Optimizer.Optimizations.TurretControlTargetSelect.SlowedRounds} ({On(config.SlowTurretSearch)}), кадров турелей без цели {IdleTurretSlowdown.Skipped} ({On(config.SlowIdleTurrets)}), " +
                   $"поисков проекций сварщиками {Optimizer.Optimizations.WelderOptimization.SlowedScans} ({On(config.SlowWelderScans)}), " +
                   $"сдвигов проекций без сварщиков рядом {ProjectionSlowdown.IdleSkipped} (всегда; проекций {ProjectionSlowdown.Known}, со сварщиками рядом {ProjectionSlowdown.WithWelders}), синхронизаций дальних гридов отложено {StateSyncSlowdown.Held} ({On(config.SlowStateSync)})";
        }

        private static string On(bool on) => on ? "вкл" : "выкл";

        /// <summary>One slowdown switched on or off by its short name; false when the name or the state is not known.</summary>
        internal static bool Toggle(SentisOptimisationsPlugin.MainConfig config, string name, string state)
        {
            bool on;
            if (state == "on") on = true;
            else if (state == "off") on = false;
            else return false;
            switch (name)
            {
                case "scripts": config.SlowScripts = on; return true;
                case "turretsearch": config.SlowTurretSearch = on; return true;
                case "idleturrets": config.SlowIdleTurrets = on; return true;
                case "welders": config.SlowWelderScans = on; return true;
                case "sync": config.SlowStateSync = on; return true;
                default: return false;
            }
        }
    }
}
