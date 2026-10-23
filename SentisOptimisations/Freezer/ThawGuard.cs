using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI;
using SentisOptimisations;
using Havok;
using Sandbox.Engine.Physics;
using VRage.Game.Entity;
using VRageMath;

namespace SentisOptimisationsPlugin.Freezer;

/// <summary>
/// A thawed group handed back to the physics quietly.
///
/// A body Havok finds deep inside the ground is pushed out of it at once, at hundreds of metres a second: a rover set down
/// with its wheels three metres and more into the rock was thrown about and torn apart (rover_spawn_probe, 23.10.2026; a
/// prod rover was thrown off a station's safe zone and wrecked at a thaw the same day). A body the freezer made fixed does
/// not collide with the ground, so whatever came between it and the ground while it was frozen shows only at the thaw.
/// Before the bodies are made dynamic again the group is lifted out of the ground if a grid of it is in it
/// (<see cref="LiftOutOfGround"/>; a single voxel's content says too little: blocks embedded half a voxel showed as in
/// the rock, and settled rovers were lifted at every thaw); and the speeds of all its grids are put to zero at the thaw and again
/// <see cref="CheckTicks"/> ticks after it (<see cref="Settle"/>). A lift, and a speed found above
/// <see cref="Quiet"/>, are logged always, not only with the freezer's logs on.
/// </summary>
internal static class ThawGuard
{
    /// <summary>Ticks after the thaw the speeds are looked at and put to zero again.</summary>
    public static readonly int[] CheckTicks = { 10, 30 };

    /// <summary>How long a grid must have been frozen for its speeds to be zeroed at the thaw, ticks (10 s).</summary>
    public const ulong MinFrozenFrames = 600;

    /// <summary>Speed (m/s, rad/s) over which a zeroing is logged.</summary>
    public const float Quiet = 0.5f;

    /// <summary>How far a group is lifted at most, metres: deeper than that it is left where it is.</summary>
    public const double MaxLift = 12;


    /// <summary>How high over where it is a grid is tried first: resting on the ground it overlaps it by its contacts.</summary>
    public const double Tolerance = 0.5;

    private const double Step = 0.25;
    private const double Clearance = 0.1;


    /// <summary>
    /// Game thread, before the bodies of <paramref name="grids"/> (a group with no static grid) are made dynamic: if a grid of
    /// them overlaps the ground even lifted <see cref="Tolerance"/> (Havok's own query of its shape against voxel bodies -
    /// a grid resting on the ground overlaps it by its contacts), the whole group is moved up along the planet's up, in
    /// steps of <see cref="Step"/>, to where none does.
    /// </summary>
    public static void LiftOutOfGround(List<MyCubeGrid> grids)
    {
        try
        {
            var moving = grids.Where(g => g != null && !g.Closed && !g.MarkedForClose && !g.IsStatic && g.Physics?.RigidBody != null).ToList();
            if (moving.Count == 0) return;
            var centre = moving[0].PositionComp.WorldAABB.Center;
            var planet = MyGamePruningStructure.GetClosestPlanet(centre);
            if (planet == null) return;
            var up = Vector3D.Normalize(centre - planet.PositionComp.GetPosition());
            var sunk = moving.FirstOrDefault(g => InGround(g, up * Tolerance));
            if (sunk == null) return;
            for (var lift = Tolerance + Step; lift <= MaxLift; lift += Step)
            {
                if (moving.Any(g => InGround(g, up * lift))) continue;
                var offset = up * (lift - Tolerance + Clearance);
                foreach (var grid in moving)
                    grid.PositionComp.SetPosition(grid.PositionComp.GetPosition() + offset);
                SentisOptimisationsPlugin.Log.Warn($"Freezer: thawing {Names(grids)}: lifted {offset.Length():0.00} m out of the ground ({sunk.DisplayName} was in it) at {centre}");
                return;
            }
            SentisOptimisationsPlugin.Log.Warn($"Freezer: thawing {Names(grids)}: {sunk.DisplayName} is more than {MaxLift} m in the ground, left where it is (at {centre})");
        }
        catch (Exception e)
        {
            SentisOptimisationsPlugin.Log.Error(e, "Freezer: lifting a thawed group out of the ground");
        }
    }

    private static readonly List<HkBodyCollision> Hits = new List<HkBodyCollision>();

    /// <summary>Whether the grid's collision shape, moved by <paramref name="offset"/>, overlaps a voxel body.</summary>
    private static bool InGround(MyCubeGrid grid, Vector3D offset)
    {
        var body = grid.Physics.RigidBody;
        var shape = body.GetShape();
        var matrix = grid.Physics.GetWorldMatrix();
        var translation = matrix.Translation + offset;
        var rotation = Quaternion.CreateFromRotationMatrix(matrix);
        Hits.Clear();
        MyPhysics.GetPenetrationsShape(shape, ref translation, ref rotation, Hits, MyPhysics.CollisionLayers.DefaultCollisionLayer);
        foreach (var hit in Hits)
            if (hit.GetCollisionEntity() is MyVoxelBase) return true;
        return false;
    }

    /// <summary>Game thread, at the thaw: the speeds of the grids to zero now and again <see cref="CheckTicks"/> ticks later.</summary>
    public static void Settle(List<MyCubeGrid> grids)
    {
        if (grids.Count == 0) return;
        Zero(grids, 0);
        var frame = (int)MySandboxGame.Static.SimulationFrameCounter;
        foreach (var ticks in CheckTicks)
        {
            var after = ticks;
            MyAPIGateway.Utilities.InvokeOnGameThread(() => Zero(grids, after), "ThawGuard", frame + ticks);
        }
    }

    private static void Zero(List<MyCubeGrid> grids, int ticks)
    {
        try
        {
            List<string> fast = null;
            foreach (var grid in grids)
            {
                var physics = grid?.Physics;
                if (grid == null || grid.Closed || grid.MarkedForClose || grid.IsStatic || physics == null || physics.RigidBody == null) continue;
                // frozen again meanwhile: the freeze put its speeds to zero itself
                if (FreezeLogic.FrozenGrids.Contains(grid.EntityId)) continue;
                var linear = physics.LinearVelocity.Length();
                var angular = physics.AngularVelocity.Length();
                if (linear > Quiet || angular > Quiet)
                    (fast ??= new List<string>()).Add($"{grid.DisplayName} {linear:0.0} m/s, {angular:0.00} rad/s");
                if (linear > 0 || angular > 0) physics.SetSpeeds(Vector3.Zero, Vector3.Zero);
            }
            if (fast != null)
                SentisOptimisationsPlugin.Log.Warn($"Freezer: thawed {Names(grids)}, {(ticks == 0 ? "at the thaw" : ticks + " ticks after it")}: speeds put to zero - {string.Join("; ", fast)}");
        }
        catch (Exception e)
        {
            SentisOptimisationsPlugin.Log.Error(e, "Freezer: zeroing the speeds of thawed grids");
        }
    }

    private static string Names(List<MyCubeGrid> grids) =>
        string.Join(", ", grids.Where(g => g != null).Select(g => g.DisplayName).Distinct().Take(4)) + (grids.Count > 4 ? $" (+{grids.Count - 4})" : "");
}
