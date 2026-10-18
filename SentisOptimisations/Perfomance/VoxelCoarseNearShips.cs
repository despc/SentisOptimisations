using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Havok;
using ParallelTasks;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Voxels;
using VRageMath;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// The rock round a big ship, where nothing else is near, collides by a coarser mesh: the game's own mesher at its
    /// LOD1 (voxels of 2 m) for the same cells of 8 m.
    ///
    /// A grid awake near the rock costs the physics by the triangles round its collision pieces: Havok works out the
    /// distance to each in its pieces' boxes every frame, touching or not. A drill ship of 152 blocks awake in its own
    /// tunnel in a 1 km asteroid (17.10.2026, physics_drill_deep_probe): 4.0 ms of its cluster's step a frame; with the
    /// cells round it coarse, 2.25 ms, drilling 2.6 ms instead of 4.4-4.9, no contact with the rock either way.
    ///
    /// Every 30 frames for each asteroid's voxel body (not planets) with something near it: the big grids awake near it
    /// - their physical group all large, no wheels, no touch with voxels for <see cref="NoTouchFrames"/> frames - have
    /// the cells within <see cref="Margin"/> m of their box made coarse, except the cells within <see cref="KeepFine"/>
    /// m of anything else that moves there (a character, a small grid, a floating object, a grid with wheels or touching
    /// the rock): those stay fine, so whoever walks, drives or lands there meets the rock as it is. A coarse mesh is made
    /// in the background and put in only if it is not empty where the fine one is not, and no vertex of it comes within
    /// <see cref="Clearance"/> m of the big grid's blocks (a coarse surface standing into the ship would throw it).
    /// A cell that stops qualifying is emptied of its coarse mesh and the game makes it fine again from the voxels as
    /// they are now (ahead of whoever comes near, as it makes any missing cell); a cell the game remade meanwhile
    /// (drilled) is left fine for <see cref="CooldownFrames"/> frames.
    ///
    /// The log has a line when the rock round a big ship turns coarse (the grid's name, id and owner, its load, how many
    /// cells) and one when it turns fine again, with why: the load under half the threshold, gone from the rock, small
    /// grids or wheels in its group, the setting off. A ship that comes to rest on the rock or falls asleep keeps the
    /// rock under it as it is (below) and has no line for it.
    ///
    /// The rock under a big grid that lies on it - touching it, or asleep - is never changed (within <see cref="Held"/> m
    /// of its box): neither made coarse nor given back, whoever comes, until the grid goes. A surface swapped under
    /// what rests on it throws it (voxel_coarse: a ship let down onto coarse rock and pressed on it); a ship that came
    /// down onto the coarse rock was clear of it by <see cref="Clearance"/> when it was made, and lies on it as it is.
    /// </summary>
    [PatchShim]
    public static class VoxelCoarseNearShips
    {
        public const double Margin = 6;
        public const double KeepFine = 16;
        public const double Clearance = 1;
        public const double Held = 2;
        public const ulong NoTouchFrames = 60;
        public const ulong CooldownFrames = 600;
        /// <summary>A cell whose coarse mesh came too near a hull is tried again after this long: the ship has gone on by then.</summary>
        public const ulong RejectedCooldownFrames = 1800;
        private const int PassEvery = 3;
        private const int JobsPerPass = 32;

        // Read by SentisTests.
        public static long CoarseCells, MadeCoarse, Rejected, Reverted, WorkTicks;
        // the game thread's share: the passes, and every voxel body's shape updates (theirs and ours alike)
        public static long PassTicks, Passes, ShapeUpdateTicks, ShapeUpdates;
        [ThreadStatic] private static long _shapeUpdateStart;
        /// <summary>The group loads are taken afresh this often (frames).</summary>
        private const ulong LoadEveryFrames = 60;

        private static bool On => SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config?.VoxelCoarseNearShips == true;
        private static double Threshold => SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config?.VoxelCoarseMinGridMs ?? double.MaxValue;

        // each grid group's share of the physics step (PhysicsCost.Share of the monitor's average), by its biggest grid's id
        private static readonly Dictionary<long, double> Loads = new Dictionary<long, double>();
        private static ulong _loadsFrame;
        private static bool _loadsTaken;
        // the groups (by the id of the grid the rock is looked at for) over the threshold, until under half of it
        private static readonly HashSet<long> Loaded = new HashSet<long>();

        /// <summary>
        /// Whether a big ship's group loads the physics enough: from the threshold on, and then until under half of it
        /// (the coarse rock itself takes its load down, and it must not switch itself off by that).
        /// </summary>
        private static bool LoadedEnough(MyCubeGrid grid, List<MyCubeGrid> group, ulong frame, out double load)
        {
            if (!_loadsTaken || frame - _loadsFrame >= LoadEveryFrames)
            {
                _loadsFrame = frame;
                _loadsTaken = true;
                Loads.Clear();
                if (PhysicsLoadMonitor.Frames > 0) PhysicsCost.Share(PhysicsLoadMonitor.AverageMs, Loads);
            }
            load = 0;
            foreach (var member in group)
                if (Loads.TryGetValue(member.EntityId, out var ms)) load += ms;
            var threshold = Threshold;
            if (load >= threshold) Loaded.Add(grid.EntityId);
            else if (load < threshold / 2) Loaded.Remove(grid.EntityId);
            if (Loaded.Count > 1024) Loaded.Clear();
            return Loaded.Contains(grid.EntityId);
        }

        /// <summary>The share of the physics step a group's grids are given now, ms (for the tests). Game thread.</summary>
        public static double LoadOf(MyCubeGrid grid)
        {
            var members = new List<IMyCubeGrid>();
            Sandbox.ModAPI.MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Physical, members);
            double load = 0;
            foreach (var member in members)
                if (Loads.TryGetValue(member.EntityId, out var ms)) load += ms;
            return load;
        }


        private sealed class BodyState
        {
            public readonly Dictionary<Vector3I, HkBvCompressedMeshShape> Coarse = new Dictionary<Vector3I, HkBvCompressedMeshShape>();
            // cell -> the frame it may be tried again from
            public readonly Dictionary<Vector3I, ulong> Cooldown = new Dictionary<Vector3I, ulong>();
            public readonly HashSet<Vector3I> Pending = new HashSet<Vector3I>();
            // the cells the last pass wanted coarse: a mesh made for a cell that is no longer among them is not put in
            public HashSet<Vector3I> Wanted = new HashSet<Vector3I>();
            // what the last pass took the grids by the rock for: "id:Free|Resting|No" each (SentisTests)
            public string Kinds = "";
            // the grids the rock is coarse round now, for the log: id -> its name as logged
            public readonly Dictionary<long, string> Round = new Dictionary<long, string>();
            public int Tick;
        }

        /// <summary>A big grid's blocks as the background check needs them: its cells and how to get from the world to them.</summary>
        private sealed class Hull
        {
            public MatrixD WorldToGrid;
            public float GridSize;
            public HashSet<Vector3I> Cells;
        }

        /// <summary>What a grid's blocks say, kept until its block count changes: wheels, and the cells they take.</summary>
        private sealed class GridInfo
        {
            public int Blocks = -1;
            public bool Wheels;
            // made anew on a change, never changed: the background check reads it
            public HashSet<Vector3I> Cells;
        }

        private static readonly ConditionalWeakTable<MyCubeGrid, GridInfo> Grids = new ConditionalWeakTable<MyCubeGrid, GridInfo>();

        private static GridInfo InfoOf(MyCubeGrid grid)
        {
            var info = Grids.GetOrCreateValue(grid);
            if (info.Blocks == grid.BlocksCount) return info;
            var wheels = false;
            foreach (var block in grid.GetFatBlocks())
                if (block is MyMotorSuspension) { wheels = true; break; }
            var cells = new HashSet<Vector3I>();
            foreach (var block in grid.CubeBlocks)
                for (var x = block.Min.X; x <= block.Max.X; x++)
                for (var y = block.Min.Y; y <= block.Max.Y; y++)
                for (var z = block.Min.Z; z <= block.Max.Z; z++)
                    cells.Add(new Vector3I(x, y, z));
            info.Wheels = wheels;
            info.Cells = cells;
            info.Blocks = grid.BlocksCount;
            return info;
        }

        private static readonly ConditionalWeakTable<MyVoxelPhysicsBody, BodyState> States = new ConditionalWeakTable<MyVoxelPhysicsBody, BodyState>();
        private static readonly List<Vector3I> Buffer = new List<Vector3I>();
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo NearbyField = typeof(MyVoxelPhysicsBody).GetField("m_nearbyEntities", Any);
        private static readonly FieldInfo MesherField = typeof(MyVoxelPhysicsBody).GetField("m_mesher", Any);
        private static readonly FieldInfo OffsetField = typeof(MyVoxelPhysicsBody).GetField("m_cellsOffset", Any);
        private static readonly MethodInfo CreateShapeMethod = typeof(MyVoxelPhysicsBody).GetMethod("CreateShape", Any);
        private static readonly MethodInfo MarkForShapeUpdateMethod = typeof(MyVoxelPhysicsBody).GetMethod("MarkForShapeUpdate", Any);
        private static MethodInfo _calculateMesh;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("VoxelCoarseNearShips", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            if (NearbyField == null || MesherField == null || OffsetField == null || CreateShapeMethod == null || MarkForShapeUpdateMethod == null)
                throw new MissingMemberException("MyVoxelPhysicsBody is not what VoxelCoarseNearShips knows");
            var update10 = typeof(MyVoxelPhysicsBody).GetMethod("UpdateAfterSimulation10", Any, null, Type.EmptyTypes, null)
                           ?? throw new MissingMethodException("MyVoxelPhysicsBody.UpdateAfterSimulation10");
            ctx.GetPattern(update10).Suffixes.Add(typeof(VoxelCoarseNearShips).GetMethod(nameof(Update10Suffix), BindingFlags.Static | BindingFlags.NonPublic));
            var shapeUpdate = typeof(MyVoxelPhysicsBody).GetMethod("UpdateRigidBodyShape", Any, null, Type.EmptyTypes, null);
            if (shapeUpdate != null)
            {
                ctx.GetPattern(shapeUpdate).Prefixes.Add(typeof(VoxelCoarseNearShips).GetMethod(nameof(ShapeUpdatePrefix), BindingFlags.Static | BindingFlags.NonPublic));
                ctx.GetPattern(shapeUpdate).Suffixes.Add(typeof(VoxelCoarseNearShips).GetMethod(nameof(ShapeUpdateSuffix), BindingFlags.Static | BindingFlags.NonPublic));
            }
        }

        private static void ShapeUpdatePrefix() => _shapeUpdateStart = System.Diagnostics.Stopwatch.GetTimestamp();

        private static void ShapeUpdateSuffix()
        {
            if (_shapeUpdateStart == 0) return;
            Interlocked.Add(ref ShapeUpdateTicks, System.Diagnostics.Stopwatch.GetTimestamp() - _shapeUpdateStart);
            Interlocked.Increment(ref ShapeUpdates);
            _shapeUpdateStart = 0;
        }

        private static void Update10Suffix(MyVoxelPhysicsBody __instance)
        {
            try
            {
                if (!(__instance.Entity is MyVoxelMap) || __instance.RigidBody == null) return;
                var state = States.GetOrCreateValue(__instance);
                if (++state.Tick % PassEvery != 0) return;
                var began = System.Diagnostics.Stopwatch.GetTimestamp();
                Pass(__instance, state);
                PassTicks += System.Diagnostics.Stopwatch.GetTimestamp() - began;
                Passes++;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "VoxelCoarseNearShips");
            }
        }

        private static void Pass(MyVoxelPhysicsBody body, BodyState state)
        {
            var voxel = (MyVoxelBase)body.Entity;
            var nearby = NearbyField.GetValue(body) as List<MyEntity>;
            // most asteroids, most of the time: nothing moves by them and nothing of theirs is coarse
            var on = On;
            if ((!on || nearby == null || nearby.Count == 0) && state.Coarse.Count == 0 && state.Pending.Count == 0 && state.Round.Count == 0) return;
            var frame = MySandboxGame.Static.SimulationFrameCounter;
            var shape = (HkUniformGridShape)body.RigidBody.GetShape();

            // the cells to have coarse: round the big grids awake there, less round everything else that moves there
            var wanted = new HashSet<Vector3I>();
            var fine = new HashSet<Vector3I>();
            var held = new HashSet<Vector3I>();
            var hulls = new List<Hull>();
            var free = new HashSet<long>();
            var kinds = new List<string>();
            // for the log: what each grid by the rock is this pass, and the big ships the rock is made coarse round
            var why = new Dictionary<long, string>();
            var round = new Dictionary<long, (MyCubeGrid Grid, List<MyCubeGrid> Group, double Load, int Cells)>();
            var resting = new HashSet<long>();
            if (nearby != null && nearby.Count > 0)
                foreach (var entity in nearby)
                {
                    if (entity == null || entity.MarkedForClose || entity.Physics == null) continue;
                    var box = entity is MyCubeGrid g ? GroupBox(g) : entity.PositionComp.WorldAABB;
                    List<MyCubeGrid> group = null;
                    var kind = entity is MyCubeGrid grid ? Kind(grid, out group) : Big.No;
                    double load = 0;
                    // a big ship under the threshold is only a neighbour: the rock by it stays fine
                    if (kind == Big.Free && !(on && LoadedEnough((MyCubeGrid)entity, group, frame, out load)))
                    {
                        why[entity.EntityId] = on ? $"its load {load:0.00} ms is under half the threshold" : "the setting is off";
                        kind = Big.No;
                    }
                    else if (kind == Big.Resting) resting.Add(entity.EntityId);
                    else if (kind == Big.No && entity is MyCubeGrid) why[entity.EntityId] = "it is no big ship now (small grids or wheels in its group)";
                    if (entity is MyCubeGrid) kinds.Add(entity.EntityId + ":" + kind);
                    if (kind == Big.Free)
                    {
                        box.Inflate(Margin);
                        var before = wanted.Count;
                        Cells(voxel, box, wanted);
                        round[entity.EntityId] = ((MyCubeGrid)entity, group, load, wanted.Count - before);
                        foreach (var member in group)
                        {
                            hulls.Add(HullOf(member));
                            free.Add(member.EntityId);
                        }
                    }
                    else if (kind == Big.Resting)
                    {
                        var rest = box;
                        rest.Inflate(Held);
                        Cells(voxel, rest, held);
                        box.Inflate(KeepFine);
                        Cells(voxel, box, fine);
                    }
                    else
                    {
                        box.Inflate(KeepFine);
                        Cells(voxel, box, fine);
                    }
                }
            wanted.ExceptWith(fine);
            wanted.ExceptWith(held);
            if (!on) wanted.Clear();
            LogChanges(voxel, state, round, why, resting, on);
            state.Wanted = wanted;
            state.Kinds = string.Join(" ", kinds);

            // coarse cells no longer wanted, or remade by the game meanwhile: back to the game's own
            var changed = false;
            foreach (var pair in state.Coarse.ToList())
            {
                var cell = pair.Key;
                var current = shape.GetChild(cell.X, cell.Y, cell.Z, out var child) ? child : default;
                var ours = current.Base.IsValid && HkShape.Comparer.Equals(current.Base, pair.Value.Base);
                if (!ours)
                {
                    state.Coarse.Remove(cell);
                    Interlocked.Decrement(ref CoarseCells);
                    state.Cooldown[cell] = frame + CooldownFrames;
                    continue;
                }
                if (wanted.Contains(cell) || held.Contains(cell) || UnderGrid(voxel, cell, free)) continue;
                shape.RemoveChild(cell.X, cell.Y, cell.Z);
                state.Coarse.Remove(cell);
                Interlocked.Decrement(ref CoarseCells);
                Interlocked.Increment(ref Reverted);
                changed = true;
            }
            foreach (var cell in state.Cooldown.Where(p => frame >= p.Value).Select(p => p.Key).ToList())
                state.Cooldown.Remove(cell);
            if (changed) MarkForShapeUpdateMethod.Invoke(body, null);

            // new coarse cells, made in the background
            if (wanted.Count == 0) return;
            var mesher = MesherField.GetValue(body);
            if (mesher == null) return;
            _calculateMesh = _calculateMesh ?? mesher.GetType().GetMethod("CalculateMesh", Any);
            var offset = (Vector3I)OffsetField.GetValue(body);
            var started = 0;
            foreach (var cell in wanted)
            {
                if (started >= JobsPerPass) break;
                if (state.Coarse.ContainsKey(cell) || state.Pending.Contains(cell) || state.Cooldown.ContainsKey(cell)) continue;
                // only a cell with a fine surface now: an empty or missing one is the game's to make
                if (!shape.GetChild(cell.X, cell.Y, cell.Z, out var fineMesh) || fineMesh.IsZero) continue;
                state.Pending.Add(cell);
                started++;
                StartCoarse(body, state, voxel, mesher, offset, cell, fineMesh, hulls);
            }
        }

        private static void StartCoarse(MyVoxelPhysicsBody body, BodyState state, MyVoxelBase voxel, object mesher, Vector3I offset, Vector3I cell,
            HkBvCompressedMeshShape fineMesh, List<Hull> hulls)
        {
            var made = default(HkBvCompressedMeshShape);
            var ok = false;
            var worldMatrix = voxel.WorldMatrix;
            var half = voxel.SizeInMetresHalf;
            Parallel.Start(() =>
            {
                var began = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    var start = (cell + offset) * 4;
                    var result = _calculateMesh.Invoke(mesher, new object[]
                    {
                        1, start - 1, start + 4 + 2, MyStorageDataTypeFlags.ContentAndMaterial,
                        MyVoxelRequestFlags.SurfaceMaterial | MyVoxelRequestFlags.ForPhysics, null
                    });
                    var mesh = result.GetType().GetProperty("Mesh")?.GetValue(result) ?? result.GetType().GetField("Mesh")?.GetValue(result);
                    if (mesh == null) return;
                    made = (HkBvCompressedMeshShape)CreateShapeMethod.Invoke(body, new[] { mesh, (object)1 });
                    if (made.IsZero) return;
                    ok = ClearOf(made, worldMatrix, half, hulls);
                }
                catch (Exception e)
                {
                    SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "VoxelCoarseNearShips: coarse mesh");
                }
                finally
                {
                    Interlocked.Add(ref WorkTicks, System.Diagnostics.Stopwatch.GetTimestamp() - began);
                }
            }, () =>
            {
                state.Pending.Remove(cell);
                if (!ok)
                {
                    if (!made.IsZero) made.Base.RemoveReference();
                    Interlocked.Increment(ref Rejected);
                    state.Cooldown[cell] = MySandboxGame.Static.SimulationFrameCounter + RejectedCooldownFrames;
                    return;
                }
                // put in only over the same fine mesh it was made for: anything else changed the cell meanwhile
                var shape = body.RigidBody == null ? default : (HkUniformGridShape)body.RigidBody.GetShape();
                if (body.RigidBody == null || !state.Wanted.Contains(cell) || !shape.GetChild(cell.X, cell.Y, cell.Z, out var current) || !HkShape.Comparer.Equals(current.Base, fineMesh.Base))
                {
                    made.Base.RemoveReference();
                    return;
                }
                shape.SetChild(cell.X, cell.Y, cell.Z, made, HkReferencePolicy.TakeOwnership);
                state.Coarse[cell] = made;
                Interlocked.Increment(ref CoarseCells);
                Interlocked.Increment(ref MadeCoarse);
                MarkForShapeUpdateMethod.Invoke(body, null);
            }, WorkPriority.Low);
        }

        /// <summary>Whether no vertex of the mesh comes within <see cref="Clearance"/> of the hulls' blocks.</summary>
        private static bool ClearOf(HkBvCompressedMeshShape mesh, MatrixD voxelWorld, Vector3 half, List<Hull> hulls)
        {
            if (hulls.Count == 0) return true;
            var geometry = new HkGeometry();
            try
            {
                mesh.GetGeometry(geometry);
                var reach = Clearance * Clearance;
                for (var i = 0; i < geometry.VertexCount; i++)
                {
                    var world = Vector3D.Transform((Vector3D)geometry.GetVertex(i) - half, voxelWorld);
                    foreach (var hull in hulls)
                    {
                        var local = Vector3D.Transform(world, hull.WorldToGrid) / hull.GridSize;
                        var near = new Vector3I((int)Math.Round(local.X), (int)Math.Round(local.Y), (int)Math.Round(local.Z));
                        var reachCells = (float)Clearance / hull.GridSize;
                        for (var x = -1; x <= 1; x++)
                        for (var y = -1; y <= 1; y++)
                        for (var z = -1; z <= 1; z++)
                        {
                            var c = near + new Vector3I(x, y, z);
                            if (!hull.Cells.Contains(c)) continue;
                            // the distance from the vertex to the block's cube, in cells
                            var dx = Math.Max(0, Math.Abs(local.X - c.X) - 0.5);
                            var dy = Math.Max(0, Math.Abs(local.Y - c.Y) - 0.5);
                            var dz = Math.Max(0, Math.Abs(local.Z - c.Z) - 0.5);
                            if ((dx * dx + dy * dy + dz * dz) * hull.GridSize * hull.GridSize < reach) return false;
                        }
                    }
                }
                return true;
            }
            finally
            {
                geometry.Dispose();
            }
        }

        /// <summary>
        /// One line when the rock round a big ship turns coarse, and one when it turns back - with the grid's name, its
        /// owner, its load and why: not a line a pass, a line a change.
        /// </summary>
        private static void LogChanges(MyVoxelBase voxel, BodyState state, Dictionary<long, (MyCubeGrid Grid, List<MyCubeGrid> Group, double Load, int Cells)> round,
            Dictionary<long, string> why, HashSet<long> resting, bool on)
        {
            var log = SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log;
            var asteroid = voxel.StorageName ?? voxel.EntityId.ToString();
            foreach (var pair in round)
            {
                if (state.Round.ContainsKey(pair.Key)) continue;
                var grid = pair.Value.Grid;
                var owner = SentisOptimisations.PlayerUtils.GetPlayerIdentity(SentisOptimisations.PlayerUtils.GetOwner(pair.Value.Group.Cast<IMyCubeGrid>().ToList()));
                var label = $"'{grid.DisplayName}' ({grid.EntityId}) of {(owner == null ? "nobody" : "'" + owner.DisplayName + "'")}";
                state.Round[pair.Key] = label;
                log.Info($"VoxelCoarseNearShips: coarse rock round {label} on {asteroid}: its load {pair.Value.Load:0.00} ms " +
                         $"(threshold {Threshold:0.00} ms), up to {pair.Value.Cells} cells of 8 m round it");
            }
            foreach (var pair in state.Round.Where(p => !round.ContainsKey(p.Key)).ToList())
            {
                // on the rock or asleep the rock under it is held as it is, coarse cells and all: nothing goes back yet
                if (on && resting.Contains(pair.Key)) continue;
                state.Round.Remove(pair.Key);
                var reason = !on ? "the setting is off" : why.TryGetValue(pair.Key, out var w) ? w : "it is not by the rock any more (gone, or frozen by the freezer)";
                log.Info($"VoxelCoarseNearShips: fine rock again round {pair.Value} on {asteroid}: {reason}");
            }
        }

        private static readonly List<MyEntity> Found = new List<MyEntity>();

        /// <summary>
        /// Whether a grid other than the free big ships has its box within <see cref="Held"/> of the cell: the voxels' list
        /// of what is near them leaves out the grids the game holds fixed (a freezer's frozen ship), and the rock under
        /// them must not change either.
        /// </summary>
        private static bool UnderGrid(MyVoxelBase voxel, Vector3I cell, HashSet<long> free)
        {
            var coord = new MyCellCoord(0, cell);
            MyVoxelCoordSystems.GeometryCellCoordToWorldAABB(voxel.PositionLeftBottomCorner, ref coord, out var box);
            box.Inflate(Held);
            Found.Clear();
            MyGamePruningStructure.GetTopMostEntitiesInBox(ref box, Found, MyEntityQueryType.Both);
            foreach (var entity in Found)
                if (entity is MyCubeGrid grid && !grid.IsStatic && !free.Contains(grid.EntityId) && grid.PositionComp.WorldAABB.Intersects(box))
                {
                    Found.Clear();
                    return true;
                }
            Found.Clear();
            return false;
        }

        private enum Big { No, Free, Resting }

        /// <summary>
        /// A grid's physical group all large and without wheels is a big ship: free when awake and clear of the voxels for
        /// <see cref="NoTouchFrames"/> frames, resting when asleep or touching them; anything else is not.
        /// </summary>
        private static Big Kind(MyCubeGrid grid, out List<MyCubeGrid> group)
        {
            group = null;
            if (grid.IsStatic || grid.GridSizeEnum != MyCubeSize.Large || grid.Physics?.RigidBody == null) return Big.No;
            var members = new List<IMyCubeGrid>();
            Sandbox.ModAPI.MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Physical, members);
            group = members.Cast<MyCubeGrid>().ToList();
            var resting = !grid.Physics.IsActive;
            foreach (var member in group)
            {
                if (member.GridSizeEnum != MyCubeSize.Large) return Big.No;
                if (InfoOf(member).Wheels) return Big.No;
                var touched = PhysicsCost.TouchedVoxels(member.EntityId, NoTouchFrames);
                if (touched == null) return Big.No; // nothing counted: not ours to judge
                if (touched == true) resting = true;
            }
            return resting ? Big.Resting : Big.Free;
        }

        private static BoundingBoxD GroupBox(MyCubeGrid grid)
        {
            var box = grid.PositionComp.WorldAABB;
            var members = new List<IMyCubeGrid>();
            Sandbox.ModAPI.MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Physical, members);
            foreach (var member in members) box.Include(member.WorldAABB);
            return box;
        }

        private static Hull HullOf(MyCubeGrid grid) =>
            new Hull { WorldToGrid = grid.PositionComp.WorldMatrixNormalizedInv, GridSize = grid.GridSize, Cells = InfoOf(grid).Cells };

        /// <summary>The voxel body's cells of 8 m (LOD0) a world box takes in.</summary>
        private static void Cells(MyVoxelBase voxel, BoundingBoxD box, HashSet<Vector3I> into)
        {
            var local = box.TransformFast(voxel.PositionComp.WorldMatrixInvScaled);
            local.Translate(voxel.SizeInMetresHalf);
            Vector3 min = local.Min, max = local.Max;
            MyVoxelCoordSystems.LocalPositionToVoxelCoord(ref min, out Vector3I vmin);
            MyVoxelCoordSystems.LocalPositionToVoxelCoord(ref max, out Vector3I vmax);
            voxel.Storage.ClampVoxelCoord(ref vmin);
            voxel.Storage.ClampVoxelCoord(ref vmax);
            MyVoxelCoordSystems.VoxelCoordToGeometryCellCoord(ref vmin, out var cmin);
            MyVoxelCoordSystems.VoxelCoordToGeometryCellCoord(ref vmax, out var cmax);
            for (var x = cmin.X; x <= cmax.X; x++)
            for (var y = cmin.Y; y <= cmax.Y; y++)
            for (var z = cmin.Z; z <= cmax.Z; z++)
                into.Add(new Vector3I(x, y, z));
        }

        /// <summary>What the last pass over a voxel body took the grids by it for (for the tests). Game thread.</summary>
        public static string KindsOf(MyVoxelBase voxel) =>
            voxel?.Physics is MyVoxelPhysicsBody body && States.TryGetValue(body, out var state) ? state.Kinds : "";

        /// <summary>The cells of a voxel body that are coarse now (for the tests). Game thread.</summary>
        public static List<Vector3I> CoarseCellsOf(MyVoxelBase voxel) =>
            voxel?.Physics is MyVoxelPhysicsBody body && States.TryGetValue(body, out var state) ? state.Coarse.Keys.ToList() : new List<Vector3I>();

    }
}
