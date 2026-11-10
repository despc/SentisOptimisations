using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using NLog;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using SpaceEngineers.Game.EntityComponents.Blocks;
using SpaceEngineers.Game.EntityComponents.GameLogic;
using Torch.Managers.PatchManager;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// A farm plot's water no longer costs two network events a frame.
    ///
    /// The plot's water tank (<see cref="MyResourceStorageComponent"/>) is filled or drained every frame while water
    /// flows, and the game sent its level to every client (<c>OnFilledRatioCallback</c>, reliable, broadcast) as soon as it
    /// moved by 0.01% of the tank - every frame. The server applies that event to itself too, the level's change calls
    /// the plot's <c>OnDetailedInfoChanged</c>, and that is a second event (its terminal text) - every frame, for every
    /// plot being watered: 2.3 s of 27.3 s of the game thread in a trace of the live server (18:33 10.11.2026), nearly all
    /// of it in sending those events.
    ///
    /// Now the server goes on counting the water every frame as before (growth, the sink, the save take its level), but
    /// tells the clients the level only when it moved by <see cref="SyncStep"/> of the tank since they were last told,
    /// reached empty or full, or <see cref="MaxFrames"/> went by since; the last of a run is sent at the tank's next
    /// update of 100 frames. Clients do not count the water, they only show it: what they show is at most 1% behind.
    /// The plot's terminal text is sent no more than once in <see cref="MaxFrames"/> a plot, the last change sent at the
    /// plot's next update of 100 frames.
    /// </summary>
    [PatchShim]
    public static class FarmPlotWaterSync
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>The move of the level, a share of the tank, that is sent at once.</summary>
        public const double SyncStep = 0.01;

        /// <summary>Frames after which a moved level, a changed terminal text, is sent whatever.</summary>
        public const int MaxFrames = 100;

        /// <summary>Levels sent / kept on the server only, terminal texts sent / held, since the start (for the stand's check).</summary>
        public static long LevelsSent, LevelsKept, InfoSent, InfoHeld;

        private sealed class Tank
        {
            public double Sent = double.NaN;
            public int Frame = int.MinValue;
        }

        private sealed class Plot
        {
            public int Frame = int.MinValue;
            public bool Pending;
        }

        private static readonly ConditionalWeakTable<MyResourceStorageComponent, Tank> Tanks = new ConditionalWeakTable<MyResourceStorageComponent, Tank>();
        private static readonly ConditionalWeakTable<MyFarmPlotLogic, Plot> Plots = new ConditionalWeakTable<MyFarmPlotLogic, Plot>();

        private static MethodInfo _sendLevel;
        private static MethodInfo _sendInfo;
        private static PropertyInfo _filled;

        [ThreadStatic] private static bool _flushing;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("FarmPlotWaterSync", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var change = typeof(MyResourceStorageComponent).GetMethod("ChangeFilledRatio", instance, null, new[] { typeof(double), typeof(bool) }, null)
                         ?? throw new MissingMethodException("MyResourceStorageComponent", "ChangeFilledRatio");
            _sendLevel = typeof(MyResourceStorageComponent).GetMethod("ChangeFillRatioAmount", instance, null, new[] { typeof(double) }, null)
                         ?? throw new MissingMethodException("MyResourceStorageComponent", "ChangeFillRatioAmount");
            _filled = typeof(MyResourceStorageComponent).GetProperty("FilledRatio", instance) ?? throw new MissingMemberException("MyResourceStorageComponent", "FilledRatio");
            var tankUpdate = typeof(MyResourceStorageComponent).GetMethod("UpdateAfterSimulation100", instance, null, Type.EmptyTypes, null)
                             ?? throw new MissingMethodException("MyResourceStorageComponent", "UpdateAfterSimulation100");
            _sendInfo = typeof(MyFarmPlotLogic).GetMethod("OnDetailedInfoChanged", instance, null, Type.EmptyTypes, null)
                        ?? throw new MissingMethodException("MyFarmPlotLogic", "OnDetailedInfoChanged");
            var plotUpdate = typeof(MyFarmPlotLogic).GetMethod("UpdateAfterSimulation100", instance, null, Type.EmptyTypes, null)
                             ?? throw new MissingMethodException("MyFarmPlotLogic", "UpdateAfterSimulation100");

            ctx.GetPattern(change).Prefixes.Add(Method(nameof(ChangePrefix)));
            ctx.GetPattern(tankUpdate).Suffixes.Add(Method(nameof(TankUpdateSuffix)));
            ctx.GetPattern(_sendInfo).Prefixes.Add(Method(nameof(InfoPrefix)));
            ctx.GetPattern(plotUpdate).Suffixes.Add(Method(nameof(PlotUpdateSuffix)));
        }

        private static MethodInfo Method(string name) => typeof(FarmPlotWaterSync).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        /// <summary>The level the clients were last told of the tank (NaN: none since the start), for the stand's check.</summary>
        public static double SentLevel(object tank) => tank is MyResourceStorageComponent t && Tanks.TryGetValue(t, out var state) ? state.Sent : double.NaN;

        private static int Frame => MySession.Static?.GameplayFrameCounter ?? 0;

        /// <summary>
        /// A level the clients would be told: told only when it moved enough, reached an end or waited long enough;
        /// otherwise set on the server alone (the game's own path for a change not synced).
        /// </summary>
        private static void ChangePrefix(MyResourceStorageComponent __instance, double newFilledRatio, ref bool updateSync)
        {
            if (!updateSync || _flushing || !Sync.IsServer) return;
            try
            {
                var tank = Tanks.GetOrCreateValue(__instance);
                var frame = Frame;
                if (double.IsNaN(tank.Sent) || Math.Abs(newFilledRatio - tank.Sent) >= SyncStep || newFilledRatio <= 0 || newFilledRatio >= 1 ||
                    frame - tank.Frame >= MaxFrames)
                {
                    tank.Sent = newFilledRatio;
                    tank.Frame = frame;
                    LevelsSent++;
                    return;
                }
                updateSync = false;
                LevelsKept++;
            }
            catch (Exception e)
            {
                Log.Error(e, "Farm plot water sync: a level change");
            }
        }

        /// <summary>The level the clients were not told yet, sent once its run of changes is over or every 100 frames.</summary>
        private static void TankUpdateSuffix(MyResourceStorageComponent __instance)
        {
            if (!Sync.IsServer || !Tanks.TryGetValue(__instance, out var tank)) return;
            try
            {
                var level = (double)_filled.GetValue(__instance);
                if (double.IsNaN(tank.Sent) || level == tank.Sent) return;
                tank.Sent = level;
                tank.Frame = Frame;
                LevelsSent++;
                _flushing = true;
                _sendLevel.Invoke(__instance, new object[] { level });
            }
            catch (Exception e)
            {
                Log.Error(e, "Farm plot water sync: sending the last level");
            }
            finally
            {
                _flushing = false;
            }
        }

        /// <summary>The plot's terminal text sent no more than once in <see cref="MaxFrames"/>; a change held is sent later.</summary>
        private static bool InfoPrefix(MyFarmPlotLogic __instance)
        {
            if (_flushing || !Sync.IsServer) return true;
            try
            {
                var plot = Plots.GetOrCreateValue(__instance);
                var frame = Frame;
                if (frame - plot.Frame >= MaxFrames)
                {
                    plot.Frame = frame;
                    plot.Pending = false;
                    InfoSent++;
                    return true;
                }
                plot.Pending = true;
                InfoHeld++;
                return false;
            }
            catch (Exception e)
            {
                Log.Error(e, "Farm plot water sync: the terminal text");
                return true;
            }
        }

        private static void PlotUpdateSuffix(MyFarmPlotLogic __instance)
        {
            if (!Sync.IsServer || !Plots.TryGetValue(__instance, out var plot) || !plot.Pending) return;
            try
            {
                plot.Pending = false;
                plot.Frame = Frame;
                InfoSent++;
                _flushing = true;
                _sendInfo.Invoke(__instance, null);
            }
            catch (Exception e)
            {
                Log.Error(e, "Farm plot water sync: sending the held terminal text");
            }
            finally
            {
                _flushing = false;
            }
        }
    }
}
