using System;
using System.Diagnostics;
using NLog;

namespace SentisOptimisations.Slowdowns
{
    /// <summary>
    /// The slowdowns' work of every frame, from the plugin's update: the peak load counted, the late patches made, and
    /// with the slowdown logs on (Logs tab) a line a minute at the peak - how much each slowdown held back.
    /// </summary>
    public static class SlowdownTick
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static long _nextLogMs = 60000;
        private static long _scripts, _turrets, _idle, _welders, _projections, _sync, _seconds;
        private static bool _poolsCapped;

        public static void Run()
        {
            if (!_poolsCapped)
            {
                _poolsCapped = true;
                Perfomance.NetworkPoolCap.Apply();
                Log.Info("Network pools capped at " + Perfomance.NetworkPoolCap.Cap + ": " + string.Join(", ", Perfomance.NetworkPoolCap.Capped));
            }
            PeakLoad.Tick();
            ProjectionSlowdown.EnsurePatched();
            ProjectionSlowdown.Refresh();
            var now = Clock.ElapsedMilliseconds;
            if (now < _nextLogMs) return;
            _nextLogMs = now + 60000;
            var config = SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config;
            long scripts = ScriptSlowdown.Skipped, turrets = Optimizer.Optimizations.TurretControlTargetSelect.SlowedRounds,
                welders = Optimizer.Optimizations.WelderOptimization.SlowedScans, idle = IdleTurretSlowdown.Skipped, projections = ProjectionSlowdown.IdleSkipped,
                sync = StateSyncSlowdown.Held, seconds = PeakLoad.PeakSeconds;
            if (config != null && config.SlowdownLogs && (seconds > _seconds || scripts > _scripts))
                Log.Info($"Slowdowns, the last minute: {seconds - _seconds} s with the slowdowns on (load now {PeakLoad.LastLoad:F0}%); scripts' runs skipped {scripts - _scripts}, " +
                         $"turret search rounds {turrets - _turrets}, idle turret frames {idle - _idle}, welder projection scans {welders - _welders}, projection moves with no welder near (always) {projections - _projections}, " +
                         $"far state syncs held {sync - _sync}");
            _scripts = scripts;
            _turrets = turrets;
            _idle = idle;
            _welders = welders;
            _projections = projections;
            _sync = sync;
            _seconds = seconds;
        }
    }
}
