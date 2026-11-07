using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;

namespace SentisOptimisationsPlugin.Freezer
{
    /// <summary>
    /// What the freezer does to a group that was moving fast when frozen, step by step (prod 07.11.2026: a ship with
    /// rotors and hinges, flying at 104 m/s and turning at 6 rad/s with its dampeners off, its pilot dead in the seat,
    /// was frozen; at the thaw its grids were at the very same 104 m/s again, the heads 5-6 m off their joints, and
    /// torn off). With the config's FreezeTraceLogs on (off by default), a group with subgrids and a grid over
    /// <see cref="FastMps"/> at its freeze is traced: every grid's speeds, whether its body is fixed, whether its client
    /// predicts it, how far each head is from its stator - at the freeze before and after, at the thaw before and after,
    /// and 1 and 10 ticks after the thaw; the first <see cref="MaxGroups"/> groups since the start.
    /// </summary>
    internal static class FreezeTrace
    {
        public const float FastMps = 10f;
        public const int MaxGroups = 30;
        private static readonly int[] AfterTicks = { 1, 10 };

        private static bool On => SentisOptimisationsPlugin.Config?.FreezeTraceLogs == true;

        private static readonly ConcurrentDictionary<long, byte> Traced = new ConcurrentDictionary<long, byte>();
        private static int _groups;

        private static NLog.Logger Log => SentisOptimisationsPlugin.Log;

        /// <summary>At the freeze, before the speeds are put to zero: the group traced from now on if a grid of it is fast.</summary>
        public static bool AtFreeze(List<MyCubeGrid> grids)
        {
            try
            {
                if (!On || grids.Count < 2) return false;
                if (!grids.Any(g => g?.Physics != null && g.Physics.LinearVelocity.Length() > FastMps)) return false;
                if (System.Threading.Interlocked.Increment(ref _groups) > MaxGroups) return false;
                foreach (var g in grids) Traced[g.EntityId] = 0;
                Write("freeze, before the speeds are zeroed", grids);
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("FreezeTrace: " + e.Message);
                return false;
            }
        }

        public static bool IsTraced(MyCubeGrid grid) => grid != null && Traced.ContainsKey(grid.EntityId);

        public static void Write(string phase, IEnumerable<MyCubeGrid> grids)
        {
            try
            {
                var list = grids.Where(g => g != null).ToList();
                var frame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
                Log.Warn($"FreezeTrace [{phase}] frame {frame}: " + string.Join("; ", list.Select(Grid)) + " | joints: " + Joints(list));
            }
            catch (Exception e)
            {
                Log.Warn("FreezeTrace: " + e.Message);
            }
        }

        /// <summary>After the thaw: the group again a few ticks later, then no longer traced.</summary>
        public static void AfterThaw(List<MyCubeGrid> grids)
        {
            var traced = grids.Where(IsTraced).ToList();
            if (traced.Count == 0) return;
            Write("thaw, after", traced);
            var frame = (int)MySandboxGame.Static.SimulationFrameCounter;
            foreach (var ticks in AfterTicks)
            {
                var t = ticks;
                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    Write("thaw +" + t + " ticks", traced);
                    if (t == AfterTicks[AfterTicks.Length - 1])
                        foreach (var g in traced) Traced.TryRemove(g.EntityId, out _);
                }, "FreezeTrace", frame + t);
            }
        }

        private static string Grid(MyCubeGrid g)
        {
            var p = g.Physics;
            if (p == null) return g.DisplayName + " no physics";
            var body = p.RigidBody;
            var at = g.PositionComp.GetPosition();
            return $"{g.DisplayName} ({g.EntityId}) v {p.LinearVelocity.Length():0.0} m/s w {p.AngularVelocity.Length():0.00} rad/s" +
                   $" [body v {(body == null ? -1 : body.LinearVelocity.Length()):0.0} w {(body == null ? -1 : body.AngularVelocity.Length()):0.00}]" +
                   $" fixed {body?.IsFixed}, static {g.IsStatic}, predicted {g.IsClientPredicted}, at ({at.X:0.0}, {at.Y:0.0}, {at.Z:0.0})";
        }

        private static string Joints(List<MyCubeGrid> grids)
        {
            var parts = new List<string>();
            foreach (var g in grids)
            foreach (var stator in g.GetFatBlocks().OfType<Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase>())
            {
                var top = stator.TopBlock;
                parts.Add(top == null
                    ? stator.CustomName + " no head"
                    : $"{stator.CustomName} {VRageMath.Vector3D.Distance(stator.PositionComp.GetPosition(), top.PositionComp.GetPosition()):0.00} m");
            }
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }
    }
}
