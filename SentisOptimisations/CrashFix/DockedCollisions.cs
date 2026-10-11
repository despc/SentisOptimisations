using System;
using System.Collections.Generic;
using System.Reflection;
using Havok;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// Grids locked together by connectors do not collide with each other, and part without a kick.
    ///
    /// The game joins a rotor's head to its stator so that the two never collide (the head's body takes the stator
    /// grid's collision system group, as a subsystem that does not collide with it: <c>MyMotorStator
    /// .CubeGrid_OnPhysicsChanged</c>); a connector's lock does not, and the two grids keep colliding. Where their hulls
    /// reach into each other - a drill head into the base, a ship docked a little crooked - the lock's constraint and the
    /// contact fight every frame: the docked grids shake. And the moment the lock lets go, the contact throws them
    /// apart: docked_shake in SentisTests threw the ship off at 10.5 m/s; on the server a ship let go of its connector
    /// flew a kilometre and a half.
    ///
    /// Here, while two dynamic grids (or a dynamic one and a static one) are locked by connectors, the lighter one does
    /// not collide with the other, as a rotor's head with its stator. When they part, it stays so while their shapes
    /// still reach into each other - they drift apart instead of being thrown - and its own collisions come back once they
    /// are clear (or after <see cref="ReleaseSeconds"/> at the most). Switched on and off in the plugin's settings
    /// (<c>DockedCollisionsOff</c>) at any time: off, every grid it holds gets its own collisions back.
    /// </summary>
    [PatchShim]
    public static class DockedCollisions
    {
        /// <summary>How long a parted grid waits for its shape to clear the other's before its collisions come back.</summary>
        public const double ReleaseSeconds = 60;

        private const int EveryFrames = 10;

        private sealed class Held
        {
            public MyCubeGrid Light, Heavy;
            public MyShipConnector Connector;
            public ulong PartedFrame;
            /// <summary>The light grid's own subsystem and the one it does not collide with, before it was held.</summary>
            public int Sub = -1, DontCollide;
        }

        private static readonly Dictionary<long, Held> Pairs = new Dictionary<long, Held>();
        private static readonly List<long> Done = new List<long>();
        private static bool _scanned;

        /// <summary>Grids whose collisions with the grid they were locked to were left out, since the start.</summary>
        public static long Applied;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("DockedCollisions", ctx, c =>
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var weld = typeof(MyShipConnector).GetMethod("Weld", any, null, new[] { typeof(MatrixD?) }, null)
                       ?? throw new MissingMethodException("MyShipConnector.Weld(MatrixD?)");
            c.GetPattern(weld).Suffixes.Add(typeof(DockedCollisions).GetMethod(nameof(WeldSuffix), BindingFlags.Static | BindingFlags.NonPublic));
        });

        private static bool On => SentisOptimisationsPlugin.Config?.DockedCollisionsOff == true;

        private static void WeldSuffix(MyShipConnector __instance)
        {
            if (!On) return;
            try { Take(__instance); }
            catch (Exception e) { SentisOptimisationsPlugin.Log.Error(e, "DockedCollisions: weld"); }
        }

        /// <summary>A locked connector's pair taken in: the lighter grid's collisions with the heavier left out.</summary>
        private static void Take(MyShipConnector connector)
        {
            var other = connector.Other;
            if (!connector.Connected || other == null) return;
            var a = connector.CubeGrid;
            var b = other.CubeGrid;
            if (a == b || a.Physics == null || b.Physics == null || a.IsStatic && b.IsStatic) return;
            var light = b.IsStatic || !a.IsStatic && a.Physics.Mass <= b.Physics.Mass ? a : b;
            var heavy = light == a ? b : a;
            if (!Pairs.TryGetValue(light.EntityId, out var held) || held.Heavy != heavy)
            {
                held = new Held { Light = light, Heavy = heavy, Connector = light == a ? connector : other };
                var info = light.Physics.RigidBody?.GetCollisionFilterInfo() ?? 0;
                held.Sub = (int)((info >> 5) & 31);
                held.DontCollide = (int)((info >> 10) & 31);
                Pairs[light.EntityId] = held;
            }
            Apply(light, heavy);
        }

        /// <summary>Every frame, game thread.</summary>
        public static void Tick()
        {
            var frame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            if (frame % EveryFrames != 0) return;
            if (!On)
            {
                if (Pairs.Count == 0) return;
                foreach (var held in Pairs.Values) Restore(held);
                Pairs.Clear();
                _scanned = false;
                return;
            }
            if (!_scanned)
            {
                // the locks made before (a world loaded, the switch turned on)
                _scanned = true;
                foreach (var entity in MyEntities.GetEntities())
                    if (entity is MyCubeGrid grid && !grid.MarkedForClose)
                        foreach (var block in grid.GetFatBlocks())
                            if (block is MyShipConnector connector && connector.Connected) Take(connector);
            }
            foreach (var pair in Pairs)
            {
                var held = pair.Value;
                try
                {
                    if (held.Light.MarkedForClose || held.Heavy.MarkedForClose || held.Light.Physics == null || held.Heavy.Physics == null)
                    {
                        Done.Add(pair.Key);
                        continue;
                    }
                    var locked = held.Connector.Connected && !held.Connector.MarkedForClose && held.Connector.Other?.CubeGrid == held.Heavy;
                    if (locked)
                    {
                        held.PartedFrame = 0;
                        Apply(held.Light, held.Heavy);     // again: a body made anew takes the game's filter back
                        continue;
                    }
                    if (held.PartedFrame == 0) held.PartedFrame = frame;
                    var waited = (frame - held.PartedFrame) / 60.0;
                    if (waited < ReleaseSeconds && Overlap(held.Light, held.Heavy)) continue;
                    Restore(held);
                    Done.Add(pair.Key);
                }
                catch (Exception e)
                {
                    SentisOptimisationsPlugin.Log.Error(e, "DockedCollisions " + pair.Key);
                    Done.Add(pair.Key);
                }
            }
            foreach (var id in Done) Pairs.Remove(id);
            Done.Clear();
        }

        /// <summary>The light grid's body in the heavy grid's collision system group, as a subsystem that does not collide with it.</summary>
        private static void Apply(MyCubeGrid light, MyCubeGrid heavy)
        {
            var body = light.Physics?.RigidBody;
            if (body == null || heavy.Physics == null) return;
            var wanted = HkGroupFilter.CalcFilterInfo(body.Layer, heavy.Physics.HavokCollisionSystemID, 1, 1);
            if (body.GetCollisionFilterInfo() == wanted) return;
            body.SetCollisionFilterInfo(wanted);
            MyPhysics.RefreshCollisionFilter(light.Physics);
            Applied++;
        }

        /// <summary>The grid's own collisions back: its own system group.</summary>
        private static void Restore(Held held)
        {
            var light = held.Light;
            var body = light.Physics?.RigidBody;
            if (body == null) return;
            var own = HkGroupFilter.CalcFilterInfo(body.Layer, light.Physics.HavokCollisionSystemID, Math.Max(0, held.Sub), held.DontCollide);
            if (body.GetCollisionFilterInfo() == own) return;
            body.SetCollisionFilterInfo(own);
            MyPhysics.RefreshCollisionFilter(light.Physics);
        }

        /// <summary>Whether the two grids' shapes still reach into each other.</summary>
        public static bool Overlap(MyCubeGrid a, MyCubeGrid b)
        {
            var ba = a.Physics?.RigidBody;
            var bb = b.Physics?.RigidBody;
            if (ba == null || bb == null) return false;
            var pa = a.Physics.ClusterToWorld(ba.Position);
            var ra = Quaternion.CreateFromRotationMatrix(ba.GetRigidBodyMatrix());
            var pb = b.Physics.ClusterToWorld(bb.Position);
            var rb = Quaternion.CreateFromRotationMatrix(bb.GetRigidBodyMatrix());
            return MyPhysics.IsPenetratingShapeShape(ba.GetShape(), ref pa, ref ra, bb.GetShape(), ref pb, ref rb);
        }
    }
}
