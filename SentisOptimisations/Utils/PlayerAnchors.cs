using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The places a player is present at: the character, and whatever the player is controlling.
    ///
    /// They are usually the same spot, but not when somebody flies a ship from a remote control
    /// block - through an antenna or a laser antenna - while their body stands in a base kilometres
    /// away. The server has to treat both as live: the ship needs its surroundings simulated and
    /// replicated so it can be flown, and the body needs its own, because that is where the player
    /// will be standing when they let go of the controls.
    ///
    /// <c>MyPlayer.GetPosition()</c> answers with the controlled entity only, so anything that used
    /// it - the freezer, among others - was thawing around the ship and leaving the player's own
    /// surroundings frozen.
    ///
    /// The positions are taken as a snapshot at most every <see cref="RefreshMs"/>, because the
    /// freezer asks this for every grid group of the world from a background loop.
    /// </summary>
    public static class PlayerAnchors
    {
        /// <summary>How stale a snapshot may be before it is taken again.</summary>
        private const int RefreshMs = 250;

        private static Vector3D[] _positions = new Vector3D[0];
        private static DateTime _taken = DateTime.MinValue;
        private static readonly object Lock = new object();

        /// <summary>Every position a player is present at right now.</summary>
        public static Vector3D[] Positions
        {
            get
            {
                var now = DateTime.UtcNow;
                lock (Lock)
                {
                    if ((now - _taken).TotalMilliseconds < RefreshMs) return _positions;
                    _positions = Take();
                    _taken = now;
                    return _positions;
                }
            }
        }

        /// <summary>Whether any player is present within <paramref name="radius"/> of the point.</summary>
        public static bool AnyInRadius(Vector3D point, double radius)
        {
            var squared = radius * radius;
            foreach (var position in Positions)
                if (Vector3D.DistanceSquared(position, point) < squared)
                    return true;

            return false;
        }

        /// <summary>The anchors of one player: the character and what it controls, when they differ.</summary>
        public static void Of(MyPlayer player, List<Vector3D> into)
        {
            if (player == null) return;

            var character = player.Character;
            if (character != null && !character.Closed && !character.MarkedForClose)
                into.Add(character.PositionComp.GetPosition());

            var controlled = player.Controller?.ControlledEntity?.Entity;
            if (controlled == null || controlled.Closed || controlled.MarkedForClose) return;

            // A ship is controlled through one of its blocks, so the whole grid is the anchor.
            var entity = controlled.GetTopMostParent() ?? controlled;
            if (entity == character) return;
            into.Add(entity.PositionComp.GetPosition());
        }

        private static Vector3D[] Take()
        {
            try
            {
                if (MySession.Static?.Players == null) return new Vector3D[0];
                var positions = new List<Vector3D>();
                foreach (var player in MySession.Static.Players.GetOnlinePlayers()) Of(player, positions);
                return positions.ToArray();
            }
            catch (Exception e)
            {
                // A player list that changes under us is not worth a freeze; the previous snapshot
                // stands until the next refresh.
                SentisOptimisationsPlugin.Log.Warn(e, "Could not take the player anchors");
                return _positions;
            }
        }
    }
}
