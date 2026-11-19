using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using VRage.Game.Entity;
using VRageMath;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// A planet builds its voxel physics only where the planet is.
    ///
    /// Every 10 frames a planet builds its physics shapes around the dynamic entities near it
    /// (<c>MyPlanet.UpdatePlanetPhysics</c>): the clusters of their boxes, each walked cell by cell - a cell a
    /// kilometre across - in <c>GeneratePhysicalShapeForBox</c>. An entity whose box in the pruning structure had
    /// grown to 45 000 km made a cluster of 1.8 trillion cells, and the stand hung in that walk for good (a full
    /// dump, 28.09.2026). The game notices such a cluster ("Too large cluster") and walks it all the same.
    ///
    /// Here the box is cut to the planet's own box first: past it there are no voxels of this planet, so nothing
    /// is lost. A box of more than 1000 km is not built at all: cut to the planet it still covers every cell of
    /// it, and the stand hung again creating physics for the whole planet (second dump, 28.09.2026). No real
    /// cluster is that large - it is a stale leaf of the pruning tree: the tree moves a leaf only when the entity
    /// leaves its fat box, so a box that was enormous for one frame stays enormous for good (a SentisAi bot's
    /// leaf 4.8 billion km across around a one-metre character). Such an entity is put into the tree anew, and
    /// what was done is written to the log once a minute.
    /// </summary>
    [PatchShim]
    public static class PlanetPhysicsBox
    {
        private const double SaneSizeM = 1000000;
        private const double MarginM = 1024 + 32;
        private static DateTime _loggedAt = DateTime.MinValue;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("PlanetPhysicsBox", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var method = typeof(MyPlanet).GetMethod("GeneratePhysicalShapeForBox", BindingFlags.Instance | BindingFlags.NonPublic, null,
                             new[] { typeof(Vector3I).MakeByRefType(), typeof(BoundingBoxD).MakeByRefType() }, null)
                         ?? throw new MissingMethodException("MyPlanet", "GeneratePhysicalShapeForBox");
            ctx.GetPattern(method).Prefixes.Add(typeof(PlanetPhysicsBox).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
            var move = typeof(MyGamePruningStructure).GetMethod("MoveInternal", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(MyEntity) }, null)
                       ?? throw new MissingMethodException("MyGamePruningStructure", "MoveInternal");
            var pattern = ctx.GetPattern(move);
            pattern.Prefixes.Add(Own(nameof(MovePrefix)));
            pattern.Transpilers.Add(Own(nameof(MoveTranspiler)));
        }

        private static DateTime _moveLoggedAt = DateTime.MinValue;
        private static bool _guardInMove;

        private static MethodInfo Own(string name) => typeof(PlanetPhysicsBox).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        private static IEnumerable<MsilInstruction> MoveTranspiler(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            _guardInMove = GuardComputedBox(list);
            if (!_guardInMove)
                SentisOptimisationsPlugin.Log.Warn("PlanetPhysicsBox: MoveInternal IL changed; keeping the separate bounds check");
            return list;
        }

        private static bool GuardComputedBox(List<MsilInstruction> list)
        {
            var getBox = typeof(MyGamePruningStructure).GetMethod(nameof(MyGamePruningStructure.GetEntityAABB), new[] { typeof(MyEntity) });
            var sites = list.Select((op, i) => (op, i)).Where(x => x.op.OpCode == OpCodes.Call &&
                x.op.Operand is MsilOperandInline<MethodBase> method && method.Value == getBox).Select(x => x.i).ToList();
            if (sites.Count != 1 || sites[0] < 5 || sites[0] + 2 >= list.Count || list.Any(x => x.TryCatchOperations.Count != 0)) return false;
            var i = sites[0];
            var exit = list[3];
            var proxy = typeof(MyEntity).GetField(nameof(MyEntity.TopMostPruningProxyId));
            if (list[0].OpCode != OpCodes.Ldarg_1 || list[1].OpCode != OpCodes.Ldfld ||
                !(list[1].Operand is MsilOperandInline<FieldInfo> field) || field.Value != proxy ||
                list[2].OpCode != OpCodes.Ldc_I4_M1 || (exit.OpCode != OpCodes.Beq && exit.OpCode != OpCodes.Beq_S) ||
                !(exit.Operand is MsilOperandBrTarget target) || !list.Any(x => x.OpCode == OpCodes.Ret && x.Labels.Contains(target.Target)) ||
                list[i - 1].OpCode != OpCodes.Ldarg_1 || list[i + 1].OpCode != OpCodes.Stloc_0 ||
                (list[i + 2].OpCode != OpCodes.Ldloca && list[i + 2].OpCode != OpCodes.Ldloca_S) ||
                !(list[i + 2].Operand is MsilOperandInline<MsilLocal> box) || box.Value.Index != 0 || box.Value.Type != typeof(BoundingBoxD)) return false;

            // Validate the actual box the game is about to insert, then reuse its existing early return.
            var reject = new MsilInstruction(OpCodes.Brfalse);
            ((MsilOperandBrTarget)reject.Operand).Target = target.Target;
            list.InsertRange(i + 2, new[]
            {
                new MsilInstruction(OpCodes.Ldarg_1),
                new MsilInstruction(OpCodes.Ldloca).InlineValue(box.Value),
                new MsilInstruction(OpCodes.Call).InlineValue((MethodBase)Own(nameof(MoveBoxAllowed))),
                reject,
            });
            return true;
        }

        /// <summary>
        /// An insane box never gets into the tree: the leaf stays where it was until the entity's box is sane again
        /// (a leaf once grown is never shrunk by the tree). Written to the log once a minute with the stack, to find
        /// who gives an entity such a box.
        /// </summary>
        private static bool MovePrefix(MyEntity entity)
        {
            if (_guardInMove) return true;
            try
            {
                if (entity.TopMostPruningProxyId == -1) return true;
                var box = MyGamePruningStructure.GetEntityAABB(entity);
                return MoveBoxAllowed(entity, ref box);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "PlanetPhysicsBox.MovePrefix");
                return true;
            }
        }

        private static bool MoveBoxAllowed(MyEntity entity, ref BoundingBoxD box)
        {
            if (Sane(box)) return true;
            try
            {
                var straighten = Scaled(entity.PositionComp.WorldMatrixRef);
                if ((DateTime.UtcNow - _moveLoggedAt).TotalSeconds > 60)
                {
                    _moveLoggedAt = DateTime.UtcNow;
                    SentisOptimisationsPlugin.Log.Error("An insane box kept out of the pruning tree" + (straighten ? " (its scaled matrix straightened)" : "") + ": " +
                                                        Describe(entity) + "\n" + Environment.StackTrace);
                }
                // a scaled matrix (a character's, built from a rotation off unit and saved so) is put right after the
                // tree's pass - setting it now would add to the list being walked
                if (straighten) Sandbox.ModAPI.MyAPIGateway.Utilities.InvokeOnGameThread(() => Straighten(entity));
                return false;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "PlanetPhysicsBox.MoveBoxAllowed");
                return false;
            }
        }

        /// <summary>The box cut to the planet's; false (nothing to build) when they do not meet.</summary>
        public static bool Cut(ref BoundingBoxD shapeBox, BoundingBoxD planetBox)
        {
            var planet = planetBox.GetInflated(MarginM);
            if (!shapeBox.Intersects(planet)) return false;
            shapeBox = shapeBox.Intersect(planet);
            return shapeBox.Valid;
        }

        /// <summary>A world matrix whose rotation is not a rotation: an axis longer or shorter than one by a lot.</summary>
        public static bool Scaled(MatrixD m)
        {
            foreach (var axis in new[] { m.Right, m.Up, m.Backward })
            {
                var length = axis.Length();
                if (!(length > 0.9 && length < 1.1)) return true;
            }
            return false;
        }

        /// <summary>The same direction and place with unit axes.</summary>
        public static MatrixD Straightened(MatrixD m)
        {
            var forward = Vector3D.Normalize(m.Forward);
            var up = Vector3D.Normalize(m.Up);
            if (!forward.IsValid() || !up.IsValid() || Math.Abs(Vector3D.Dot(forward, up)) > 0.999)
                return MatrixD.CreateTranslation(m.Translation);
            return MatrixD.CreateWorld(m.Translation, forward, Vector3D.Normalize(Vector3D.Reject(up, forward)));
        }

        private static void Straighten(MyEntity entity)
        {
            if (entity.MarkedForClose || !Scaled(entity.PositionComp.WorldMatrixRef)) return;
            var m = Straightened(entity.PositionComp.WorldMatrixRef);
            entity.PositionComp.SetWorldMatrix(ref m, null, true);
            SentisOptimisationsPlugin.Log.Warn("PlanetPhysicsBox: straightened the scaled matrix of " + entity.DebugName + " (" + entity.EntityId + "), box now " +
                                               entity.PositionComp.WorldAABB.Size.Max().ToString("F1") + " m");
        }

        /// <summary>A box no real cluster exceeds; NaN is not sane either.</summary>
        public static bool Sane(BoundingBoxD box)
        {
            var size = box.Size.Max();
            return size <= SaneSizeM;
        }

        private static bool Prefix(MyPlanet __instance, ref BoundingBoxD shapeBox)
        {
            try
            {
                var planetBox = __instance.PositionComp.WorldAABB;
                if (!Sane(shapeBox))
                {
                    var report = Reseat(__instance, shapeBox, planetBox);
                    if ((DateTime.UtcNow - _loggedAt).TotalSeconds > 60)
                    {
                        _loggedAt = DateTime.UtcNow;
                        SentisOptimisationsPlugin.Log.Error(report);
                    }
                    return false;
                }
                return Cut(ref shapeBox, planetBox);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "PlanetPhysicsBox");
                return true;
            }
        }

        private static readonly FieldInfo TreeField = typeof(MyGamePruningStructure).GetField("m_dynamicObjectsTree", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// The owners of the insane clusters, asked of the pruning tree itself (a query by entities finds an entity
        /// by its current box, not by its leaf); each one whose own box is sane goes back into the tree anew.
        /// </summary>
        private static string Reseat(MyPlanet planet, BoundingBoxD shapeBox, BoundingBoxD planetBox)
        {
            var text = "A planet's physics box of " + (shapeBox.Size.Max() / 1000).ToString("F0") + " km at " + planet.StorageName + " skipped";
            try
            {
                var tree = (MyDynamicAABBTreeD)TreeField.GetValue(MyGamePruningStructure.Instance);
                var boxes = new List<BoundingBoxD>();
                var nodes = new List<VRage.MyTuple<bool, int, object>>();
                var query = planetBox;
                tree.GetAproximateClustersForAabbDebug(ref query, 512.0, boxes, nodes);
                var shown = 0;
                for (var i = 0; i < boxes.Count && shown < 3; i++)
                {
                    if (Sane(boxes[i])) continue;
                    shown++;
                    var entity = nodes[i].Item3 as MyEntity;
                    text += "; " + (nodes[i].Item1 ? "leaf " : "node of " + nodes[i].Item2 + " leaves ") + boxes[i] + " of " + Describe(entity);
                    if (!nodes[i].Item1 || entity == null || entity.MarkedForClose || !Sane(MyGamePruningStructure.GetEntityAABB(entity))) continue;
                    MyGamePruningStructure.Remove(entity);
                    MyGamePruningStructure.Add(entity);
                    text += ", put into the tree anew";
                }
                if (shown == 0) text += "; no insane cluster in the tree now";
            }
            catch (Exception e) { text += "; tree query failed: " + e.Message; }
            return text;
        }

        private static string Describe(MyEntity e)
        {
            if (e == null) return "nobody";
            var top = e;
            while (top.Parent != null) top = top.Parent;
            return e.DebugName + " (" + e.GetType().Name + " " + e.EntityId + ", top " + top.DisplayName + " " + top.EntityId + ")" +
                   ", entity pruning AABB " + MyGamePruningStructure.GetEntityAABB(e) + ", world AABB " + e.PositionComp.WorldAABB +
                   ", local AABB " + e.PositionComp.LocalAABB + ", matrix " + e.PositionComp.WorldMatrixRef +
                   ", velocity " + e.Physics?.LinearVelocity + ", physics enabled " + e.Physics?.Enabled +
                   ", marked for close " + e.MarkedForClose + ", closed " + e.Closed + ", in scene " + e.InScene;
        }
    }
}
