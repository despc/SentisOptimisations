using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;

namespace SentisOptimisations.Commands
{
    /// <summary>
    /// Live characters without an identity of their own, for <c>!ai_npc ghosts</c>. Seated ones are found through the
    /// seats: a pilot is not among the world's entities. Game thread only.
    /// </summary>
    public static class CharacterGhosts
    {
        public enum Kind
        {
            None,
            /// <summary>No player and no identity: out in the world, its weapon position's update throws every frame.</summary>
            NoIdentity,
            /// <summary>
            /// The identity exists but neither controls nor holds the body. Identity ids are handed out in order from the
            /// highest one loaded, so after a restart a removed bot's number goes to a new identity and its leftover body
            /// looks owned again. A body that a mod or plugin made without <c>ChangeCharacter</c> looks the same, so these
            /// are removed only when asked for explicitly.
            /// </summary>
            NotHeld,
        }

        public sealed class Ghost
        {
            public MyCharacter Character;
            public MyCockpit Seat;
            public Kind Kind;
            public string Reason;
        }

        public static Kind Classify(bool hasIdentity, bool controlled, bool heldAsCharacter, bool inSavedCharacters)
        {
            if (!hasIdentity) return Kind.NoIdentity;
            return controlled || heldAsCharacter || inSavedCharacters ? Kind.None : Kind.NotHeld;
        }

        /// <summary>A plain clean removes only the bodies that break the update; <paramref name="includeNotHeld"/> asks for the rest.</summary>
        public static bool Removes(Kind kind, bool includeNotHeld) => kind == Kind.NoIdentity || (includeNotHeld && kind == Kind.NotHeld);

        public static List<Ghost> Find()
        {
            var ghosts = new List<Ghost>();
            var seen = new HashSet<long>();

            void Consider(MyCharacter character, MyCockpit seat)
            {
                if (character == null || character.IsDead || character.MarkedForClose || character.Closed || !seen.Add(character.EntityId))
                    return;
                // the same test as the weapon position's guard: the controlling player's identity first, then the owner's
                var identity = character.GetIdentity();
                var kind = Classify(identity != null, character.ControllerInfo?.Controller != null,
                    identity?.Character == character, identity?.SavedCharacters?.Contains(character.EntityId) == true);
                if (kind == Kind.None) return;
                ghosts.Add(new Ghost
                {
                    Character = character,
                    Seat = seat,
                    Kind = kind,
                    Reason = kind == Kind.NoIdentity ? "no identity" : "identity " + identity.IdentityId + " '" + identity.DisplayName + "' does not hold it",
                });
            }

            foreach (var entity in MyEntities.GetEntities().ToList())
            {
                if (entity is MyCharacter character)
                {
                    Consider(character, null);
                    continue;
                }
                if (!(entity is MyCubeGrid grid) || grid.MarkedForClose)
                    continue;
                foreach (var cockpit in grid.GetFatBlocks<MyCockpit>())
                {
                    Consider(cockpit.IsBeingUsedBy as MyCharacter, cockpit);
                    // a pilot RemovePilot let go of without detaching it stays the seat's child
                    var hierarchy = cockpit.Hierarchy;
                    if (hierarchy == null)
                        continue;
                    List<MyCharacter> seated = null;
                    foreach (var child in hierarchy.Children)
                        if (child?.Container?.Entity is MyCharacter childCharacter)
                            (seated ?? (seated = new List<MyCharacter>())).Add(childCharacter);
                    if (seated != null)
                        foreach (var childCharacter in seated)
                            Consider(childCharacter, cockpit);
                }
            }
            return ghosts;
        }

        public static string Where(Ghost ghost) => ghost.Seat == null
            ? "in the world"
            : "in '" + ghost.Seat.DisplayNameText + "' (" + ghost.Seat.EntityId + ") on '" + ghost.Seat.CubeGrid?.DisplayName + "' (" + ghost.Seat.CubeGrid?.EntityId + ")";

        /// <summary>Takes the body out of its seat and closes it; null when done, else what went wrong.</summary>
        public static Exception Remove(Ghost ghost, Action<Exception> seatFailed)
        {
            var character = ghost.Character;
            // the body is closed even when taking it out of its seat threw (the seat lets go of it on close)
            try
            {
                if (ghost.Seat != null && ghost.Seat.IsBeingUsedBy == character)
                    ghost.Seat.RemovePilot();
            }
            catch (Exception e)
            {
                seatFailed?.Invoke(e);
            }
            try
            {
                if (!character.MarkedForClose)
                    character.Close();
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }
    }
}
