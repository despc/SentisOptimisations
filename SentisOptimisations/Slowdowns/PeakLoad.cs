using System;
using System.Diagnostics;
using NLog;

namespace SentisOptimisations.Slowdowns
{
    /// <summary>
    /// Whether the server is loaded enough for the slowdowns (the settings' Slowdowns group): systems that do less while
    /// the server cannot keep up.
    ///
    /// The load is the one the clients are shown as the server's: the game thread's time of a frame against the
    /// 16.67 ms of one (<c>MySandboxGame.CPULoad</c>), here averaged over each second. 100% - the server just keeps real
    /// time; 200% - a frame takes twice its time and the game runs at half speed. A second with the load over
    /// <see cref="Settings.Percent"/> (70%) turns the slowdowns on, one under <see cref="Settings.OffPercent"/> (60%)
    /// turns them off; between the two they stay as they were (hysteresis: the slowdowns lower the load themselves, and
    /// with one threshold would go on and off every second). "Always" keeps them on (to try
    /// them), the master switch off keeps them off.
    /// </summary>
    public static class PeakLoad
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public const double FrameMs = 1000.0 / 60;

        /// <summary>The settings, read every second.</summary>
        public static Func<Settings> Config = () => Settings.FromPlugin();

        private static volatile bool _busy;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static long _secondStartMs = -1;
        private static int _secondFrames;
        private static double _secondLoad;

        /// <summary>The slowdowns on: the load of the last second over the threshold (or always, when so set).</summary>
        public static bool Active => _busy;

        /// <summary>The same as <see cref="Active"/>.</summary>
        public static bool Busy => _busy;

        /// <summary>The server load of the last whole second, % (100: a frame takes its 16.67 ms), NaN before the first.</summary>
        public static double LastLoad { get; private set; } = double.NaN;

        /// <summary>The simulation speed of the last whole second (1 = real time), NaN before the first.</summary>
        public static double LastSpeed { get; private set; } = double.NaN;

        /// <summary>How many times the slowdowns came on, and the seconds they were on, since the start.</summary>
        public static long Peaks, PeakSeconds;

        public sealed class Settings
        {
            public bool Enabled;
            public bool Always;
            public int Percent = 70;
            public int OffPercent = 60;
            public bool Logs;

            public static Settings FromPlugin()
            {
                var config = SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config;
                if (config == null) return new Settings { Enabled = false };
                return new Settings
                {
                    Enabled = config.SlowdownsEnabled,
                    Always = config.SlowdownsAlways,
                    Percent = config.SlowdownsLoadPercent,
                    OffPercent = Math.Min(config.SlowdownsOffLoadPercent, config.SlowdownsLoadPercent),
                    Logs = config.SlowdownLogs,
                };
            }
        }

        /// <summary>Once a frame, from the plugin's update on the game thread.</summary>
        public static void Tick() => Tick(Clock.ElapsedMilliseconds, Sandbox.MySandboxGame.Static?.CPULoad ?? 0);

        /// <param name="frameLoad">the load of the frame before, % (<c>MySandboxGame.CPULoad</c>)</param>
        public static void Tick(long nowMs, double frameLoad)
        {
            if (_secondStartMs < 0)
            {
                _secondStartMs = nowMs;
                _secondFrames = 0;
                _secondLoad = 0;
                return;
            }
            _secondFrames++;
            _secondLoad += frameLoad;
            var elapsed = nowMs - _secondStartMs;
            if (elapsed < 1000) return;
            var speed = Math.Min(1.0, _secondFrames * FrameMs / elapsed);
            var load = _secondLoad / _secondFrames;
            _secondStartMs = nowMs;
            _secondFrames = 0;
            _secondLoad = 0;
            Second(load, speed, Config() ?? new Settings { Enabled = false });
        }

        /// <summary>One second at the load given (%): the slowdowns on or off as the settings say.</summary>
        public static void Second(double load, double speed, Settings settings)
        {
            LastLoad = load;
            LastSpeed = speed;
            var was = _busy;
            bool now;
            if (!settings.Enabled) now = false;
            else if (settings.Always || load > settings.Percent) now = true;
            else if (load < settings.OffPercent) now = false;
            else now = was;
            _busy = now;
            if (now) PeakSeconds++;
            if (now == was) return;
            if (now) Peaks++;
            if (settings.Logs)
                Log.Info(now ? $"Slowdowns on: the server load {load:F0}% (over {settings.Percent}%{(settings.Always ? ", always on in the settings" : "")})"
                    : $"Slowdowns off: the server load {load:F0}% (under {settings.OffPercent}%)");
        }

        /// <summary>For tests: back to the start.</summary>
        public static void Reset()
        {
            _busy = false;
            _secondStartMs = -1;
            _secondFrames = 0;
            _secondLoad = 0;
            LastSpeed = double.NaN;
            LastLoad = double.NaN;
        }

        /// <summary>
        /// An entity's turn among <paramref name="period"/> frames: true on one frame in that many, a different one for
        /// different entities, so that what is slowed down does not all run in the same frame.
        /// </summary>
        public static bool Turn(long entityId, ulong frame, int period)
        {
            if (period <= 1) return true;
            var phase = Mix((ulong)entityId) % (ulong)period;
            return frame % (ulong)period == phase;
        }

        // the bits of an id mixed (splitmix64's finaliser): ids made one after another, or sharing their high bits, get
        // phases spread evenly
        private static ulong Mix(ulong x)
        {
            x ^= x >> 30;
            x *= 0xbf58476d1ce4e5b9UL;
            x ^= x >> 27;
            x *= 0x94d049bb133111ebUL;
            x ^= x >> 31;
            return x;
        }
    }
}
