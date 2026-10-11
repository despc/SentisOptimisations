using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using VRageMath;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// Grids locked together by connectors do not shake on their dampeners.
    ///
    /// The dampeners of a grid stop its own motion with all the thrust they have the moment it moves faster than a
    /// millimetre a second (<c>MyEntityThrustComponent.ComputeBaseThrust</c>). A grid locked to another by a connector is
    /// held where it is by the lock's fixed constraint, which leaves it a few millimetres a second of play: the dampeners
    /// kick against the lock, the lock throws it back, and so on every frame. On the server ("Бур" on "Опорник Бурмалда",
    /// 11.10.2026) the drill's twelve thrusters fired up to 2.3 kN at rest and it shook at up to 7.5 mm/s; with its
    /// dampeners off, 1.2 mm/s (connector_shake in SentisTests). Players see it as the docked ship shaking, sometimes hard.
    ///
    /// Here a grid locked by a connector to another dynamic grid has no dampening (gravity is still held, and a pilot's
    /// own thrust is untouched) while its whole physical group moves slower than <see cref="RestMps"/>; faster, the
    /// dampeners work as before. Switched on and off in the plugin's settings (<c>DockedDampenersCalm</c>) at any time.
    /// </summary>
    [PatchShim]
    public static class DockedDampeners
    {
        /// <summary>The speed of the whole locked group under which it counts as standing.</summary>
        public const double RestMps = 0.05;

        /// <summary>How long a grid's lock found is believed, frames.</summary>
        private const int RecheckFrames = 60;

        private static readonly Dictionary<long, (ulong Frame, bool Docked)> Docked = new Dictionary<long, (ulong, bool)>();

        /// <summary>Dampenings left out since the start, for the statistics and the tests.</summary>
        public static long Calmed;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("DockedDampeners", ctx, c =>
        {
            var type = typeof(MyCubeGrid).Assembly.GetType("Sandbox.Game.EntityComponents.MyThrusterBlockThrustComponent", true);
            var update = type.GetMethod("UpdateThrusts", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                             new[] { typeof(bool), typeof(Vector3) }, null)
                         ?? throw new MissingMethodException("MyThrusterBlockThrustComponent.UpdateThrusts(bool, Vector3)");
            c.GetPattern(update).Prefixes.Add(typeof(DockedDampeners).GetMethod(nameof(UpdateThrustsPrefix), BindingFlags.Static | BindingFlags.NonPublic));
        });

        private static void UpdateThrustsPrefix(object __instance, ref bool enableDampeners)
        {
            if (!enableDampeners || SentisOptimisationsPlugin.Config?.DockedDampenersCalm != true) return;
            if (!(((VRage.Game.Components.MyEntityComponentBase)__instance).Entity is MyCubeGrid grid) || grid.Physics == null || grid.IsStatic) return;
            if (!IsDocked(grid)) return;
            if (GroupSpeed(grid) >= RestMps) return;
            enableDampeners = false;
            Calmed++;
        }

        /// <summary>Whether a connector of the grid is locked to another dynamic grid; believed for <see cref="RecheckFrames"/>.</summary>
        public static bool IsDocked(MyCubeGrid grid)
        {
            var frame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            lock (Docked)
            {
                if (Docked.TryGetValue(grid.EntityId, out var known) && frame - known.Frame < RecheckFrames) return known.Docked;
                if (Docked.Count > 10000) Docked.Clear();
            }
            var docked = false;
            foreach (var block in grid.GetFatBlocks())
            {
                if (!(block is MyShipConnector connector) || !connector.Connected) continue;
                var other = connector.Other?.CubeGrid;
                if (other != null && other != grid && !other.IsStatic && other.Physics != null)
                {
                    docked = true;
                    break;
                }
            }
            lock (Docked) Docked[grid.EntityId] = (frame, docked);
            return docked;
        }

        /// <summary>The speed of the physical group's centre of mass.</summary>
        public static double GroupSpeed(MyCubeGrid grid)
        {
            var group = MyCubeGridGroups.Static.Physical.GetGroup(grid);
            if (group == null) return grid.Physics.LinearVelocity.Length();
            var momentum = Vector3D.Zero;
            double mass = 0;
            foreach (var node in group.Nodes)
            {
                var physics = node.NodeData.Physics;
                if (physics == null || node.NodeData.IsStatic) continue;
                momentum += (Vector3D)physics.LinearVelocity * physics.Mass;
                mass += physics.Mass;
            }
            return mass <= 0 ? 0 : (momentum / mass).Length();
        }
    }
}
