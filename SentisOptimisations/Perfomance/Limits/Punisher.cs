using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations;
using SentisOptimisations.DelayedLogic;
using VRage.Game.ModAPI;
using VRage.Library.Utils;
using VRage.Network;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    public class Punisher
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static Punisher __instance = new Punisher();

        /// <summary>
        /// A group that goes quiet for this long starts counting from scratch: without it a grid that
        /// crosses the threshold once a day is eventually converted into a station for no current load.
        /// </summary>
        private static readonly TimeSpan CounterLifetime = TimeSpan.FromMinutes(10);

        private sealed class Counter
        {
            public int Hits;
            public DateTime LastHit;
        }

        private readonly Dictionary<long, Counter> _timeToFix = new Dictionary<long, Counter>();
        private readonly Dictionary<long, Counter> _timeToAlarm = new Dictionary<long, Counter>();

        public void AlertPlayerGrid(List<IMyCubeGrid> grids)
        {
            var minEntityId = grids.Min(grid => grid.EntityId);
            string gridNames = string.Join(", ", grids.Select(grid => grid.DisplayName));
            var ownerId = PlayerUtils.GetOwner(grids);
            var playerIdentity = PlayerUtils.GetPlayerIdentity(ownerId);
            var playerName = playerIdentity == null ? "---" : playerIdentity.DisplayName;

            Log.Warn("Grid(s) " + gridNames + " of player " + playerName + " make some physics problems");
            if (playerIdentity == null) return;

            if (Hit(_timeToAlarm, minEntityId) > SentisOptimisationsPlugin.Config.PhysicsChecksBeforePunish / 2)
            {
                var warning = "Внимание. Грид(ы) " + gridNames + " заметно нагружают физику сервера.\n" +
                              "Если нагрузка вырастет, постройка будет превращена в станцию";
                Tell(ownerId, warning, 5000);
                _timeToAlarm.Remove(minEntityId);
            }
        }

        public void PunishPlayerGrid(List<IMyCubeGrid> grids)
        {
            var minEntityId = grids.Min(grid => grid.EntityId);
            string gridNames = string.Join(", ", grids.Select(grid => grid.DisplayName));
            var ownerId = PlayerUtils.GetOwner(grids);
            var playerIdentity = PlayerUtils.GetPlayerIdentity(ownerId);
            var playerName = playerIdentity == null ? "---" : playerIdentity.DisplayName;
            var attention = "Внимание! Грид(ы) " + gridNames + " ОЧЕНЬ сильно нагружают физику сервера";
            Tell(ownerId, attention, 10000);

            Log.Error("Grid " + gridNames + " of player " + playerName + " make lot of physics problems");

            if (Hit(_timeToFix, minEntityId) <= SentisOptimisationsPlugin.Config.PhysicsChecksBeforePunish) return;

            _timeToFix.Remove(minEntityId);
            ConvertGroupToStatic(grids);
        }

        public void PunishPlayerGridImmediately(List<IMyCubeGrid> grids)
        {
            ConvertGroupToStatic(grids);

            string gridNames = string.Join(", ", grids.Select(grid => grid.DisplayName));
            var ownerId = PlayerUtils.GetOwner(grids);
            var playerIdentity = PlayerUtils.GetPlayerIdentity(ownerId);
            var playerName = playerIdentity == null ? "---" : playerIdentity.DisplayName;
            Log.Error("Grid(s) " + gridNames + " of player " + playerName + " make lot of physics problems");
        }

        /// <summary>
        /// Writes to the owner and to everyone of the owner's faction, in chat and on screen. Nobody when there is
        /// no owner: to the game a chat message for player 0 is a message for all.
        /// </summary>
        private static void Tell(long ownerId, string message, int notificationMs)
        {
            if (ownerId == 0) return;
            var to = new HashSet<long> { ownerId };
            var faction = MySession.Static.Factions.GetPlayerFaction(ownerId);
            if (faction != null)
                foreach (var member in faction.Members.Keys)
                    if (member != 0) to.Add(member);
            foreach (var id in to)
            {
                ChatUtils.SendTo(id, message);
                MyVisualScriptLogicProvider.ShowNotification(message, notificationMs, "Red", id);
            }
        }

        /// <summary>
        /// Counts one hit of the group and returns the count. A group that was last seen longer than
        /// <see cref="CounterLifetime"/> ago starts from one again, and counters of groups nobody has
        /// heard of since then are dropped, so the dictionaries do not grow with the world's age.
        /// </summary>
        private int Hit(Dictionary<long, Counter> counters, long key)
        {
            var now = DateTime.UtcNow;
            if (counters.Count > 0)
            {
                var stale = counters.Where(pair => now - pair.Value.LastHit > CounterLifetime).Select(pair => pair.Key).ToList();
                foreach (var old in stale) counters.Remove(old);
            }

            if (!counters.TryGetValue(key, out var counter))
            {
                counter = new Counter();
                counters[key] = counter;
            }

            counter.Hits++;
            counter.LastHit = now;
            return counter.Hits;
        }

        /// <summary>
        /// Converts the group and, a moment later, repairs it once. The grids handed in are already one
        /// group, so there is nothing to walk: the previous version looped over grid groups but removed
        /// the wrong list from its working set, and ended up repairing a single grid of a single group.
        /// </summary>
        private void ConvertGroupToStatic(List<IMyCubeGrid> grids)
        {
            foreach (var grid in grids) ConvertToStatic((MyCubeGrid)grid);

            var first = grids.FirstOrDefault(grid => !grid.MarkedForClose);
            if (first == null) return;
            DelayedProcessor.Instance.AddDelayedAction(DateTime.Now.AddMilliseconds(MyRandom.Instance.Next(300, 2000)),
                () => MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    try
                    {
                        if (!first.MarkedForClose) FixShipLogic.FixGroupByGrid((MyCubeGrid)first);
                    }
                    catch (Exception e)
                    {
                        Log.Warn(e, "Fix of a punished group failed");
                    }
                }));
        }

        private void ConvertToStatic(MyCubeGrid myCubeGrid)
        {
            if (myCubeGrid.IsStatic || myCubeGrid.MarkedForClose) return;

            myCubeGrid.Physics?.SetSpeeds(Vector3.Zero, Vector3.Zero);
            myCubeGrid.ConvertToStatic();
            try
            {
                // Broadcast: the default endpoint already reaches every client.
                MyMultiplayer.RaiseEvent<MyCubeGrid>(myCubeGrid, (MyCubeGrid x) => new Action(x.ConvertToStatic),
                    default(EndpointId));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "()Exception in RaiseEvent.");
            }

            Tell(PlayerUtils.GetOwner(myCubeGrid),
                "Грид " + myCubeGrid.DisplayName + " превращён в станцию: он сильно нагружал сервер", 10000);

            Log.Error("Grid " + myCubeGrid.DisplayName + " Converted To Static");
        }
    }
}
