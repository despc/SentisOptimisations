using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using NLog;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// No collision damage from a grid that is leaving the world.
    ///
    /// Prod, 03.11.2026: ALE ShipFixer's !fixship (cut and paste) on "Puffy" broke its subgrids. On the stand
    /// (SentisTests fixship_puffy, the player's blueprint and ShipFixer itself): ShipFixer closes the ship and makes its
    /// copy in the same place, but a closed grid leaves the world only at the frame's end - for that frame the copy
    /// stands inside its original, and the physics hit them against each other. The hits destroyed small warheads
    /// (unarmed - a destroyed warhead goes off all the same), and the torpedoes of 104 warheads each went up in a chain:
    /// 500-800 of 3340 blocks lost, subgrids in pieces, heads off. The same without SentisClusters (fewer hits) and
    /// pasted one grid at a time; the same ship spawned from its blueprint took nothing.
    ///
    /// So Deformation (collision) damage done by a grid marked for close or closed is taken away, and by an attacker no
    /// longer found to a grid added in the last <see cref="Config.MainConfig.PasteCollisionGuardSeconds"/> of game time.
    /// Ramming by live grids, weapons, explosions: as ever (reg_collision*). With it the fixship above kept every block.
    /// </summary>
    public static class PasteCollisionGuard
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        // a grid's entity id -> the game time (ms) it was added at
        private static readonly ConcurrentDictionary<long, long> AddedAt = new ConcurrentDictionary<long, long>();
        // hits taken away per grid hit
        private static readonly ConcurrentDictionary<long, int> Taken = new ConcurrentDictionary<long, int>();
        private static bool _registered;

        /// <summary>Collision hits taken away since the start.</summary>
        public static long Suppressed;

        private static bool On => SentisOptimisationsPlugin.Config?.PasteCollisionGuard ?? true;

        private static int WindowMs => (int)((SentisOptimisationsPlugin.Config?.PasteCollisionGuardSeconds ?? 5) * 1000);

        /// <summary>At the session's load: the damage handler and the watch of grids added.</summary>
        public static void OnLoaded()
        {
            MyEntities.OnEntityAdd -= OnEntityAdd;
            MyEntities.OnEntityAdd += OnEntityAdd;
            if (_registered || MyAPIGateway.Session?.DamageSystem == null) return;
            _registered = true;
            MyAPIGateway.Session.DamageSystem.RegisterBeforeDamageHandler(0, BeforeDamage);
        }

        private static void OnEntityAdd(VRage.Game.Entity.MyEntity entity)
        {
            if (entity is MyCubeGrid grid && On) AddedAt[grid.EntityId] = MySandboxGame.TotalGamePlayTimeInMilliseconds;
        }

        private static void BeforeDamage(object target, ref MyDamageInformation info)
        {
            try
            {
                if (info.Type != MyDamageType.Deformation || info.Amount <= 0 || info.AttackerId == 0 || !On) return;
                var grid = (target as IMySlimBlock)?.CubeGrid as MyCubeGrid;
                if (grid == null) return;
                if (MyEntities.TryGetEntityById(info.AttackerId, out var attacker, true))
                {
                    var other = attacker as MyCubeGrid ?? (attacker as MyCubeBlock)?.CubeGrid;
                    var leaving = attacker.MarkedForClose || attacker.Closed || other != null && (other.MarkedForClose || other.Closed);
                    if (!leaving) return;
                }
                else if (!(AddedAt.TryGetValue(grid.EntityId, out var at) && MySandboxGame.TotalGamePlayTimeInMilliseconds - at < WindowMs))
                    return;
                info.Amount = 0;
                System.Threading.Interlocked.Increment(ref Suppressed);
                Taken.AddOrUpdate(grid.EntityId, 1, (_, n) => n + 1);
            }
            catch (Exception e)
            {
                Log.Error(e, "PasteCollisionGuard failed");
            }
        }

        private static int _tick;

        /// <summary>Every frame on the game thread: once a second, the hits taken away written out, old entries dropped.</summary>
        public static void Tick()
        {
            if (++_tick % 60 != 0) return;
            if (!Taken.IsEmpty)
            {
                var written = new Dictionary<string, int>();
                foreach (var id in new List<long>(Taken.Keys))
                {
                    if (!Taken.TryRemove(id, out var hits) || hits == 0) continue;
                    MyEntities.TryGetEntityById(id, out var entity);
                    var grid = entity as MyCubeGrid;
                    var ship = grid == null ? "grid " + id : Biggest(grid);
                    written.TryGetValue(ship, out var was);
                    written[ship] = was + hits;
                }
                foreach (var pair in written)
                    Log.Info("Collision damage to '" + pair.Key + "' from grids leaving the world (a cut and paste) taken away: " + pair.Value + " hits");
            }
            if (AddedAt.IsEmpty) return;
            var now = MySandboxGame.TotalGamePlayTimeInMilliseconds;
            var window = Math.Max(WindowMs, 1000);
            foreach (var pair in AddedAt)
                if (now - pair.Value >= window) AddedAt.TryRemove(pair.Key, out _);
        }

        private static string Biggest(MyCubeGrid grid)
        {
            var biggest = grid;
            var group = MyCubeGridGroups.Static.Physical.GetGroup(grid);
            if (group != null)
                foreach (var node in group.Nodes)
                    if (node.NodeData.BlocksCount > biggest.BlocksCount) biggest = node.NodeData;
            return biggest.DisplayName + " (" + biggest.EntityId + ")";
        }
    }
}
