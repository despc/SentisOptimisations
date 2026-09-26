using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Havok;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using VRageMath;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Ship drill cut-outs rebuild the voxel collision in the background instead of inside the
    /// physics step.
    ///
    /// A drill cut-out ends with the voxel storage reporting the changed range, and the voxel
    /// physics body drops every collision cell in it from the Havok grid shape
    /// (<c>HkUniformGridShape.InvalidateRange</c>), then starts background jobs to mesh them again.
    /// The drilling ship is touching exactly those cells, so the very next physics step asks for
    /// them before any job is done, and Havok builds them synchronously through the blocking shape
    /// request: dual contouring plus the planet material lookups, with the game thread waiting.
    /// With 32 drilling ships this was ~50 ms physics steps every few frames.
    ///
    /// During a ship drill cut-out a collision cell that already holds a surface keeps it until its
    /// background job replaces it (the job itself is vanilla: the same mesher, and the result is
    /// put in by the same completion callback). This is safe because drilling only removes rock:
    /// the old surface encloses less empty space than the new one, so for a few frames something
    /// can rest on rock that is already gone, never fall into rock that is still there. Cells
    /// without a surface keep the vanilla handling: a cell that is still all air or all rock after
    /// the cut has no surface either and is left alone, and a cell that gets its first surface
    /// (the drill broke into solid rock under it) is dropped and built as before, because an empty
    /// stale cell would let the ship fall through that rock.
    ///
    /// The hand drill's cut-outs (and explosions', the same cut-out) are handled the same way: a
    /// bot digging with the hand drill cut once a second and got a 7-12 ms physics step each time.
    ///
    /// Everything else that changes voxels - voxel hands, filling, the admin tools - is untouched.
    /// </summary>
    [PatchShim]
    public static class DrillCutPhysics
    {
        private static readonly Type ClusterCutOutType = typeof(MyShipDrill).Assembly
            .GetType("Sandbox.Game.GameSystems.MyShipMiningSystem", true)
            .GetNestedType("ClusterCutOut", BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly Type VoxelPhysicsBodyType = typeof(MyShipDrill).Assembly
            .GetType("Sandbox.Engine.Voxels.MyVoxelPhysicsBody", true);

        private static bool _inCut;
        private static ulong _cutFrame;
        private static readonly Vector3I[] SingleCell = new Vector3I[8];
        // Voxel bounds (planet storage coordinates) of the cut-outs of the finishing cluster that
        // changed rock; null when they could not be read, and then every cell counts as touched.
        private static List<BoundingBoxI> _cutBounds;
        private static readonly List<BoundingBoxI> CutBoundsBuffer = new List<BoundingBoxI>();
        private static readonly Action<object, List<BoundingBoxI>> CollectCutBounds = BuildCollector();
        // Kept cells waiting for their background mesh, and finished meshes of kept cells held
        // (with a reference of ours) until the body's next 10-frame update puts them in.
        private static readonly HashSet<(object Body, Vector3I Cell)> KeptPending = new HashSet<(object, Vector3I)>();
        private static readonly Dictionary<object, Dictionary<Vector3I, HkBvCompressedMeshShape>> HeldMeshes =
            new Dictionary<object, Dictionary<Vector3I, HkBvCompressedMeshShape>>();
        private const int KeptPendingLimit = 100000;
        private static readonly Func<object, HkRigidBody> GetRigidBody0 = body => ((MyPhysicsBody)body).RigidBody;
        private static readonly MethodInfo MarkForShapeUpdateMethod =
            VoxelPhysicsBodyType.GetMethod("MarkForShapeUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Action<object> MarkForShapeUpdate = BuildMarkForShapeUpdate();

        // Counters for the benchmark: cells kept until their job is done, and cells handled as vanilla.
        public static long KeptCells;
        public static long InvalidatedCells;
        public static long UnchangedEmptyCells;
        public static long UntouchedCells;
        public static long HeldMeshCount;
        public static long ShapeUpdatesApplied;

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("DrillCutPhysics", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            var finish = ClusterCutOutType.GetMethod("Finish", instance);
            var invalidate = VoxelPhysicsBodyType.GetMethod("InvalidateRange", instance, null,
                new[] { typeof(Vector3I), typeof(Vector3I), typeof(int) }, null);
            if (finish == null || invalidate == null)
                throw new MissingMethodException("DrillCutPhysics: ClusterCutOut.Finish or MyVoxelPhysicsBody.InvalidateRange not found");
            var pattern = ctx.GetPattern(finish);
            pattern.Prefixes.Add(typeof(DrillCutPhysics).GetMethod(nameof(FinishPrefix), statics));
            pattern.Suffixes.Add(typeof(DrillCutPhysics).GetMethod(nameof(FinishSuffix), statics));
            ctx.GetPattern(invalidate).Transpilers.Add(typeof(DrillCutPhysics).GetMethod(nameof(InvalidateRangeTranspiler), statics));
            // the hand drill's cut-outs (and explosions'): the notify the cut-out queues to the game thread
            var notify = HandCutNotify.Value;
            if (notify != null)
            {
                ctx.GetPattern(notify.Value.Method).Prefixes.Add(typeof(DrillCutPhysics).GetMethod(nameof(HandCutPrefix), statics));
                ctx.GetPattern(notify.Value.Method).Suffixes.Add(typeof(DrillCutPhysics).GetMethod(nameof(FinishSuffix), statics));
            }
            else NLog.LogManager.GetCurrentClassLogger().Warn("DrillCutPhysics: no MyVoxelGenerator.<CutOutShapeWithProperties> notify; the hand drill's cut-outs stay vanilla");
            if (MarkForShapeUpdate == null) return;
            var taskComplete = VoxelPhysicsBodyType.GetMethod("OnTaskComplete", instance);
            var update10 = VoxelPhysicsBodyType.GetMethod("UpdateAfterSimulation10", instance, null, Type.EmptyTypes, null);
            if (taskComplete == null || update10 == null) return;
            ctx.GetPattern(taskComplete).Prefixes.Add(typeof(DrillCutPhysics).GetMethod(nameof(OnTaskCompletePrefix), statics));
            ctx.GetPattern(update10).Suffixes.Add(typeof(DrillCutPhysics).GetMethod(nameof(UpdateAfterSimulation10Suffix), statics));
        }

        private static Action<object> BuildMarkForShapeUpdate()
        {
            if (MarkForShapeUpdateMethod == null) return null;
            var method = new DynamicMethod("MarkForShapeUpdate", null, new[] { typeof(object) }, VoxelPhysicsBodyType, true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, VoxelPhysicsBodyType);
            il.Emit(OpCodes.Call, MarkForShapeUpdateMethod);
            il.Emit(OpCodes.Ret);
            return (Action<object>)method.CreateDelegate(typeof(Action<object>));
        }

        /// <summary>
        /// A background mesh for a kept cell is done. Vanilla puts it into the grid shape and marks the
        /// rigid body for a shape update (HkRigidBody.UpdateShape, several milliseconds for a planet
        /// section) - with a stream of jobs finishing one by one that is an update nearly every frame.
        /// The mesh is held instead, with a reference of ours, and the old surface stays until the
        /// body's next 10-frame update, which puts in every held mesh and marks the body once: the
        /// same two steps as vanilla, batched. Anything else goes the vanilla way.
        /// </summary>
        private static bool OnTaskCompletePrefix(object __instance, VRage.Voxels.MyCellCoord coord, HkBvCompressedMeshShape childShape)
        {
            if (coord.Lod != 0 || MySandboxGame.Static?.UpdateThread != Thread.CurrentThread ||
                !KeptPending.Remove((__instance, coord.CoordInLod)))
                return true;
            if (!HeldMeshes.TryGetValue(__instance, out var held))
                HeldMeshes[__instance] = held = new Dictionary<Vector3I, HkBvCompressedMeshShape>();
            if (held.TryGetValue(coord.CoordInLod, out var older)) Release(older);
            if (!childShape.IsZero) childShape.Base.AddReference();
            held[coord.CoordInLod] = childShape;
            HeldMeshCount++;
            return false;
        }

        private static void UpdateAfterSimulation10Suffix(object __instance)
        {
            if (HeldMeshes.Count == 0 || !HeldMeshes.TryGetValue(__instance, out var held)) return;
            HeldMeshes.Remove(__instance);
            var rigidBody = GetRigidBody0(__instance);
            if (rigidBody == null)
            {
                foreach (var mesh in held.Values) Release(mesh);
                return;
            }
            var grid = (HkUniformGridShape)rigidBody.GetShape();
            var anySurface = false;
            foreach (var pair in held)
            {
                grid.SetChild(pair.Key.X, pair.Key.Y, pair.Key.Z, pair.Value, HkReferencePolicy.None);
                anySurface |= !pair.Value.IsZero;
                Release(pair.Value);
            }
            if (!anySurface) return;
            ShapeUpdatesApplied++;
            MarkForShapeUpdate(__instance);
        }

        private static void Release(HkBvCompressedMeshShape mesh)
        {
            if (!mesh.IsZero) mesh.Base.RemoveReference();
        }

        /// <summary>Drops held meshes of cells in a range that is being invalidated the vanilla way.</summary>
        private static void DropHeld(object body, Vector3I min, Vector3I max)
        {
            if (HeldMeshes.Count == 0 || !HeldMeshes.TryGetValue(body, out var held)) return;
            List<Vector3I> drop = null;
            foreach (var cell in held.Keys)
                if (cell.X >= min.X && cell.X <= max.X && cell.Y >= min.Y && cell.Y <= max.Y && cell.Z >= min.Z && cell.Z <= max.Z)
                    (drop ?? (drop = new List<Vector3I>())).Add(cell);
            if (drop == null) return;
            foreach (var cell in drop)
            {
                Release(held[cell]);
                held.Remove(cell);
            }
        }

        /// <summary>
        /// <c>foreach (ref var c in cutOut.m_cutOuts) if (c.CausedChange) output.Add(c.StorageBounds);</c>
        /// over the private pooled array of cut-out data.
        /// </summary>
        private static Action<object, List<BoundingBoxI>> BuildCollector()
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var cutOutsField = ClusterCutOutType?.GetField("m_cutOuts", instance);
            var dataType = ClusterCutOutType?.GetNestedType("CutOutData", BindingFlags.Public | BindingFlags.NonPublic);
            var memoryType = cutOutsField?.FieldType;
            var length = memoryType?.GetField("Length", instance);
            var item = memoryType?.GetMethod("get_Item", instance);
            var bounds = dataType?.GetField("StorageBounds", instance);
            var changed = dataType?.GetField("CausedChange", instance);
            if (length == null || item == null || bounds == null || changed == null) return null;

            var method = new DynamicMethod("CollectCutBounds", null, new[] { typeof(object), typeof(List<BoundingBoxI>) },
                ClusterCutOutType, true);
            var il = method.GetILGenerator();
            var index = il.DeclareLocal(typeof(int));
            var entry = il.DeclareLocal(dataType.MakeByRefType());
            Label head = il.DefineLabel(), body = il.DefineLabel(), next = il.DefineLabel();
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, index);
            il.Emit(OpCodes.Br, head);
            il.MarkLabel(body);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, ClusterCutOutType);
            il.Emit(OpCodes.Ldflda, cutOutsField);
            il.Emit(OpCodes.Ldloc, index);
            il.Emit(OpCodes.Call, item);
            il.Emit(OpCodes.Stloc, entry);
            il.Emit(OpCodes.Ldloc, entry);
            il.Emit(OpCodes.Ldfld, changed);
            il.Emit(OpCodes.Brfalse, next);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloc, entry);
            il.Emit(OpCodes.Ldfld, bounds);
            il.Emit(OpCodes.Callvirt, typeof(List<BoundingBoxI>).GetMethod(nameof(List<BoundingBoxI>.Add)));
            il.MarkLabel(next);
            il.Emit(OpCodes.Ldloc, index);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, index);
            il.MarkLabel(head);
            il.Emit(OpCodes.Ldloc, index);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, ClusterCutOutType);
            il.Emit(OpCodes.Ldflda, cutOutsField);
            il.Emit(OpCodes.Ldfld, length);
            il.Emit(OpCodes.Blt, body);
            il.Emit(OpCodes.Ret);
            return (Action<object, List<BoundingBoxI>>)method.CreateDelegate(typeof(Action<object, List<BoundingBoxI>>));
        }

        /// <summary>
        /// Whether a cut-out changed a voxel the mesh of this cell is built from. The storage reports
        /// whole 16-voxel chunks, so most cells of the reported range did not change at all. A cell's
        /// mesh reads voxels from one before to two after it, and the voxel body widens a change
        /// by two before and one after; two voxels of margin on each side cover both.
        /// </summary>
        private static bool Touched(Vector3I cell, Vector3I storageMin)
        {
            if (_cutBounds == null) return true;
            var min = cell * 8 + storageMin - 2;
            var max = min + 8 + 4;
            foreach (var b in _cutBounds)
                if (b.Min.X <= max.X && b.Max.X >= min.X && b.Min.Y <= max.Y && b.Max.Y >= min.Y &&
                    b.Min.Z <= max.Z && b.Max.Z >= min.Z)
                    return true;
            return false;
        }

        private static void FinishPrefix(object __instance)
        {
            _cutFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            _inCut = true;
            _cutBounds = null;
            if (CollectCutBounds == null) return;
            CutBoundsBuffer.Clear();
            CollectCutBounds(__instance, CutBoundsBuffer);
            _cutBounds = CutBoundsBuffer;
        }

        private static void FinishSuffix() => _inCut = false;

        /// <summary>
        /// The lambda <c>MyVoxelGenerator.CutOutShapeWithProperties</c> queues to the game thread once a cut-out is
        /// written: <c>voxelMap.Storage.NotifyChanged(minCorner, maxCorner, ...)</c>, the storage range it changed
        /// kept in its closure. The hand drill cuts once a second through it (and an explosion once): the voxel body
        /// dropped every cell of the range, and the next physics step built the ones the character stands in (and
        /// its drill's sensor rays reach) synchronously - 7-12 ms physics steps once a second while a bot digs.
        /// </summary>
        private static readonly Lazy<(MethodInfo Method, FieldInfo Outer, FieldInfo Min, FieldInfo Max)?> HandCutNotify =
            new Lazy<(MethodInfo, FieldInfo, FieldInfo, FieldInfo)?>(() =>
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                var generator = typeof(MyShipDrill).Assembly.GetType("Sandbox.Engine.Voxels.MyVoxelGenerator", true);
                foreach (var closure in generator.GetNestedTypes(BindingFlags.NonPublic))
                foreach (var method in closure.GetMethods(any))
                {
                    if (!method.Name.StartsWith("<CutOutShapeWithProperties>") || method.GetParameters().Length != 0) continue;
                    // the corners in the lambda's own closure, or in the outer one it holds (CS$<>8__locals1)
                    foreach (var outer in new FieldInfo[] { null }.Concat(closure.GetFields(any)))
                    {
                        var holder = outer == null ? closure : outer.FieldType;
                        var min = holder.GetField("minCorner", any);
                        var max = holder.GetField("maxCorner", any);
                        if (min?.FieldType == typeof(Vector3I) && max?.FieldType == typeof(Vector3I)) return (method, outer, min, max);
                    }
                }
                return null;
            });

        private static void HandCutPrefix(object __instance)
        {
            var notify = HandCutNotify.Value.Value;
            var holder = notify.Outer == null ? __instance : notify.Outer.GetValue(__instance);
            if (holder == null) return;
            _cutFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            _inCut = true;
            CutBoundsBuffer.Clear();
            CutBoundsBuffer.Add(new BoundingBoxI((Vector3I)notify.Min.GetValue(holder), (Vector3I)notify.Max.GetValue(holder)));
            _cutBounds = CutBoundsBuffer;
            if (++HandCuts % 300 == 0)
                NLog.LogManager.GetCurrentClassLogger().Info($"DrillCutPhysics: {HandCuts} hand cut-outs; cells kept till rebuilt {KeptCells}, dropped the vanilla way {InvalidatedCells}, untouched {UntouchedCells}, shape updates batched {ShapeUpdatesApplied}");
        }

        public static long HandCuts;

        /// <summary>
        /// Replaces <c>gridShape.InvalidateRange(ref min, ref max, buffer)</c> with
        /// <c>InvalidateCells(ref gridShape, ref min, ref max, buffer, this, lod)</c>.
        /// </summary>
        private static IEnumerable<MsilInstruction> InvalidateRangeTranspiler(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            var target = typeof(HkUniformGridShape).GetMethod(nameof(HkUniformGridShape.InvalidateRange));
            var replacement = typeof(DrillCutPhysics).GetMethod(nameof(InvalidateCells), BindingFlags.Static | BindingFlags.Public);
            var replaced = 0;
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].OpCode != OpCodes.Call || !(list[i].Operand is MsilOperandInline<MethodBase> call) || call.Value != target)
                    continue;
                var labels = list[i].Labels.ToList();
                var loadThis = new MsilInstruction(OpCodes.Ldarg_0);
                foreach (var label in labels) loadThis.Labels.Add(label);
                list[i] = new MsilInstruction(OpCodes.Call).InlineValue((MethodBase)replacement);
                list.Insert(i, new MsilInstruction(OpCodes.Ldarg_3));
                list.Insert(i, loadThis);
                i += 2;
                replaced++;
            }
            if (replaced != 1)
                throw new InvalidOperationException("DrillCutPhysics: expected one HkUniformGridShape.InvalidateRange call, found " + replaced);
            return list;
        }

        /// <summary>
        /// Drop-in for <see cref="HkUniformGridShape.InvalidateRange"/>: returns the cells to start
        /// background jobs for. Outside a drill cut-out it is the vanilla call.
        /// </summary>
        public static int InvalidateCells(ref HkUniformGridShape shape, ref Vector3I min, ref Vector3I max,
            Vector3I[] buffer, object body, int lod)
        {
            if (!_inCut || lod != 0 || MySandboxGame.Static == null ||
                MySandboxGame.Static.SimulationFrameCounter != _cutFrame ||
                MySandboxGame.Static.UpdateThread != Thread.CurrentThread ||
                !(((MyPhysicsBody)body).Entity is MyVoxelBase voxel) || voxel.Storage == null)
            {
                if (lod == 0) DropHeld(body, min, max);
                return shape.InvalidateRange(ref min, ref max, buffer);
            }

            var count = 0;
            var storageMin = voxel.StorageMin;
            for (var z = min.Z; z <= max.Z; z++)
            for (var y = min.Y; y <= max.Y; y++)
            for (var x = min.X; x <= max.X; x++)
            {
                var cell = new Vector3I(x, y, z);
                if (!Touched(cell, storageMin))
                {
                    // Reported only because it shares a 16-voxel chunk with a cut: same rock, same mesh.
                    UntouchedCells++;
                    continue;
                }
                if (shape.GetChild(x, y, z, out _))
                {
                    // Has a surface: keep it, rebuild in the background.
                    buffer[count++] = cell;
                    KeptCells++;
                    if (MarkForShapeUpdate != null)
                    {
                        if (KeptPending.Count >= KeptPendingLimit) KeptPending.Clear();
                        KeptPending.Add((body, cell));
                    }
                    continue;
                }
                if (shape.GetMissingCellsInRange(ref cell, ref cell, SingleCell) > 0)
                {
                    // Not built yet: vanilla would only hand it to a job as well.
                    buffer[count++] = cell;
                    continue;
                }
                // Built and empty: all air or all rock. Same test as the voxel body's own
                // prefetch (MyVoxelPhysicsBody.UpdateAfterSimulation10) for leaving a cell empty.
                var box = new BoundingBoxI(cell * 8, (cell + 1) * 8);
                box.Translate(storageMin);
                if (voxel.Storage.Intersect(ref box, 0) != ContainmentType.Intersects)
                {
                    UnchangedEmptyCells++;
                    continue;
                }
                var single = cell;
                DropHeld(body, single, single);
                var dropped = shape.InvalidateRange(ref single, ref single, SingleCell);
                for (var i = 0; i < dropped; i++) buffer[count++] = SingleCell[i];
                InvalidatedCells++;
            }
            return count;
        }
    }
}
