using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRageMath;

namespace SentisOptimisationsPlugin.Freezer
{
    /// <summary>
    /// The bodies of a grid's blocks that the blocks hold to the grid with constraints of their own, kept where they
    /// are while the freezer holds the grid fixed.
    ///
    /// A connector has two bodies of its own (the phantoms that find another connector and things to pull in), a
    /// collector one, a door one for each leaf: dynamic bodies, each tied to the grid's body by a fixed constraint
    /// (<c>MyShipConnector.CreateBodyConstraint</c>). When the freezer makes the grid's body fixed, the block takes its
    /// constraint off (the grid's physics changed) and puts it back at the thaw - but the body itself stays dynamic and
    /// keeps the speed the ship had. In a world still stepped (a player 2 km away, prod) it flew on at that speed the
    /// whole time the grid stood: 2 km in 20 s at 104 m/s. At the thaw the connector tied the grid to it where it was,
    /// and the constraint threw the whole ship after it: 104 m/s and 6.4 rad/s in a few ticks, the heads of its rotors
    /// and hinges torn off (prod 07.11.2026, the stand's freezer_moving_pilot, step=keep).
    ///
    /// So at the freeze these bodies are stopped with the grid, and at the thaw, before the grid's body is dynamic
    /// again, each one found away from its place by the grid is put back there.
    /// </summary>
    internal static class HeldBodies
    {
        private sealed class Held
        {
            public MyPhysicsBody Body;
            public HkRigidBody Rigid;
            /// <summary>The body's pose relative to the grid's at the freeze (both in Havok's world, whatever its offset).</summary>
            public Matrix ToGrid;
            public string Name;
        }

        private static readonly ConcurrentDictionary<long, List<Held>> ByGrid = new ConcurrentDictionary<long, List<Held>>();
        private static int _stoppedTold, _putBackTold;

        /// <summary>Off its place by more than this at the thaw: put back, m.</summary>
        private const float Slack = 0.05f;

        /// <summary>The dynamic bodies of the grid's own blocks tied to its body by a constraint (before the body is made fixed: the blocks take their constraints off on it).</summary>
        public static List<MyPhysicsBody> Collect(MyCubeGrid grid)
        {
            var found = new List<MyPhysicsBody>();
            try
            {
                if (!(grid.Physics is MyPhysicsBody physics) || physics.RigidBody == null) return found;
                foreach (var constraint in physics.Constraints)
                {
                    var other = Other(constraint, physics);
                    if (other == null || other.IsFixed || other.GetMotionType() == HkMotionType.Keyframed) continue;
                    if (!(other.UserObject is MyPhysicsBody body) || body == physics || found.Contains(body)) continue;
                    if (!OfGrid(body.Entity as MyEntity, grid)) continue;
                    found.Add(body);
                }
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn(e, "Freezer: the bodies held to " + grid.DisplayName);
            }
            return found;
        }

        /// <summary>After the grid's body is made fixed: the bodies stopped, and their places by the grid kept for the thaw.</summary>
        public static void Stop(MyCubeGrid grid, List<MyPhysicsBody> bodies)
        {
            if (bodies.Count == 0)
            {
                ByGrid.TryRemove(grid.EntityId, out _);
                return;
            }
            try
            {
                var gridRigid = grid.Physics?.RigidBody;
                if (gridRigid == null) return;
                var gridMatrix = gridRigid.GetRigidBodyMatrix();
                var inverse = Matrix.Invert(gridMatrix);
                var held = new List<Held>();
                var fastest = 0f;
                foreach (var body in bodies)
                {
                    var rigid = body.RigidBody;
                    if (rigid == null || !rigid.InWorld) continue;
                    fastest = Math.Max(fastest, rigid.LinearVelocity.Length());
                    rigid.LinearVelocity = Vector3.Zero;
                    rigid.AngularVelocity = Vector3.Zero;
                    held.Add(new Held { Body = body, Rigid = rigid, ToGrid = rigid.GetRigidBodyMatrix() * inverse, Name = Name(body) });
                }
                ByGrid[grid.EntityId] = held;
                if (fastest > 1f)
                {
                    var n = Interlocked.Increment(ref _stoppedTold);
                    if (n <= 10 || n % 100 == 0)
                        SentisOptimisationsPlugin.Log.Info($"Freezer: {grid.DisplayName} frozen with {held.Count} bodies of its blocks held to it by their constraints ({string.Join(", ", held.Select(h => h.Name).Distinct())}), " +
                                                           $"moving at up to {fastest:0.0} m/s: stopped with it - left moving, they fly off while it stands and the thaw ties the grid to them where they are (#{n} since the start)");
                }
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn(e, "Freezer: stopping the bodies held to " + grid.DisplayName);
            }
        }

        /// <summary>Before the grid's body is made dynamic again: each body found away from its place by the grid put back there, at rest.</summary>
        public static void PutBack(MyCubeGrid grid)
        {
            if (!ByGrid.TryRemove(grid.EntityId, out var held)) return;
            try
            {
                var gridRigid = grid.Physics?.RigidBody;
                if (gridRigid == null || !gridRigid.InWorld) return;
                var gridMatrix = gridRigid.GetRigidBodyMatrix();
                var moved = new List<string>();
                foreach (var h in held)
                {
                    // the same body, still in the grid's world (a block rebuilt or taken off meanwhile made its own anew)
                    if (h.Body.RigidBody != h.Rigid || !h.Rigid.InWorld || h.Body.HavokWorld != grid.Physics.HavokWorld) continue;
                    var place = h.ToGrid * gridMatrix;
                    var off = (h.Rigid.Position - place.Translation).Length();
                    var speed = h.Rigid.LinearVelocity.Length();
                    if (off > Slack)
                    {
                        h.Rigid.SetWorldMatrix(place);
                        moved.Add($"{h.Name} {off:0.0} m off, at {speed:0.0} m/s");
                    }
                    h.Rigid.LinearVelocity = Vector3.Zero;
                    h.Rigid.AngularVelocity = Vector3.Zero;
                }
                if (moved.Count > 0)
                {
                    var n = Interlocked.Increment(ref _putBackTold);
                    if (n <= 20 || n % 100 == 0)
                        SentisOptimisationsPlugin.Log.Warn($"Freezer: thawing {grid.DisplayName}, bodies of its blocks were away from their places by it: {string.Join("; ", moved)}. " +
                                                           $"Put back before the thaw - there, the blocks' constraints would have thrown the grid after them (#{n} since the start)");
                }
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn(e, "Freezer: putting back the bodies held to " + grid.DisplayName);
            }
        }

        public static void Forget(long gridId) => ByGrid.TryRemove(gridId, out _);

        private static HkRigidBody Other(HkConstraint constraint, MyPhysicsBody grid)
        {
            var a = constraint.RigidBodyA;
            var b = constraint.RigidBodyB;
            bool Mine(HkRigidBody body) => body != null && (body == grid.RigidBody || body == grid.RigidBody2);
            if (Mine(a)) return Mine(b) ? null : b;
            return Mine(b) ? a : null;
        }

        /// <summary>A block of the grid, or a subpart of one (a door's leaf): not another grid, not a character on a ladder.</summary>
        private static bool OfGrid(MyEntity entity, MyCubeGrid grid)
        {
            for (var e = entity; e != null; e = e.Parent)
            {
                if (e is MyCubeGrid owner) return owner == grid;
                if (e is Sandbox.Game.Entities.Character.MyCharacter) return false;
            }
            return false;
        }

        private static string Name(MyPhysicsBody body)
        {
            var entity = body.Entity as MyEntity;
            var block = entity as Sandbox.Game.Entities.MyCubeBlock ?? entity?.Parent as Sandbox.Game.Entities.MyCubeBlock;
            return block != null ? $"{block.BlockDefinition?.Id.TypeId.ToString().Replace("MyObjectBuilder_", "")} '{block.DisplayNameText}'" : entity?.GetType().Name ?? "?";
        }
    }
}
