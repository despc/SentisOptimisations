using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using SentisOptimisationsPlugin.Freezer;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using VRageMath;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// Every head that comes off a rotor, a hinge, a piston or a wheel is written to the log with why and in what state,
    /// and a head the game tore off itself is put back on when that hits nothing.
    ///
    /// Reported from the server (03.11.2026): the turrets of ships standing frozen came off - on the stand the freezer
    /// could not be caught at it. The game takes a head off by itself in <c>CheckSafetyDetach</c>, when the two points of
    /// the joint are further apart than the block's "Safety detach" (5 m for a rotor); a player takes it off in the
    /// terminal (<c>DetachRequest</c>), a script through <c>IMyMechanicalConnectionBlock.Detach</c>; the rest is a block
    /// destroyed or ground away, a grid split. Each is written once (<c>HeadDetachLog</c>): the cause, the stator, its
    /// grid and owner, how far the joint was stretched, the speeds of both grids, whether either was frozen and how long
    /// after the freezer's thaw, the nearest player, the speed of the simulation.
    ///
    /// A head torn off by the game (<c>HeadReattach</c>) is attached again once it can be done quietly: its stator and
    /// it still there and free, the head not further than <see cref="MaxDistance"/> from where it belongs, moving
    /// against the stator's grid slower than <see cref="MaxRelativeSpeed"/>. Attaching moves the head's whole physical
    /// group - the turret on the head comes with it - onto the stator's axis (the game's own <c>AlignGridsForAttach</c>;
    /// the turn about the axis is kept). Before that, the same move is worked out here and every grid of the group is
    /// tested where it would land: its shape against every other grid near it (the pair of the stator's grid and the
    /// head's own, which never collide, aside), against the voxels, and no character inside it. Anything in the way and
    /// it waits, tries again every <see cref="TryEveryFrames"/> frames, and gives up after <see cref="GiveUpFrames"/>. A
    /// group anchored somewhere (a static grid, a locked landing gear) is not put back: the game would move the stator's
    /// ship onto it instead. Rotors and hinges only (a piston's or a wheel's head is written out, not put back).
    /// </summary>
    [PatchShim]
    public static class HeadDetachWatch
    {
        public const double MaxRelativeSpeed = 3;
        public const double MaxRelativeTurn = 1.5;
        public const int TryEveryFrames = 10;
        public const int GiveUpFrames = 60 * 30;
        /// <summary>A head put back that is off again within this many frames is written out.</summary>
        public const int VerifyFrames = 120;

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static float MaxDistance => SentisOptimisationsPlugin.Config?.HeadReattachMaxDistanceM ?? 10f;

        private enum Cause { Other, Safety, Player, Script }

        [ThreadStatic] private static Cause _cause;

        private sealed class Pending
        {
            public MyMotorStator Stator;
            public MyAttachableTopBlockBase Head;
            public ulong Since, Next;
            public string Waiting;
        }

        private sealed class Verify
        {
            public MyMotorStator Stator;
            public MyAttachableTopBlockBase Head;
            public ulong At;
        }

        private static readonly ConcurrentQueue<Pending> Incoming = new ConcurrentQueue<Pending>();
        private static readonly List<Pending> Waiting = new List<Pending>();
        private static readonly List<Verify> Verifying = new List<Verify>();

        /// <summary>Heads that came off, put back, given up on (for the tests and the statistics).</summary>
        public static long Detached, SafetyDetached, PutBack, GivenUp, OffAgain;

        private static readonly PropertyInfo Constraint = typeof(MyMechanicalConnectionBlockBase).GetProperty("Constraint", Any);
        private static readonly MethodInfo TryAttach = typeof(MyMechanicalConnectionBlockBase).GetMethod("TryAttach", Any, null,
            new[] { typeof(MyAttachableTopBlockBase), typeof(bool) }, null);

        private static bool LogOn => SentisOptimisationsPlugin.Config?.HeadDetachLog != false;
        private static bool ReattachOn => SentisOptimisationsPlugin.Config?.HeadReattach == true;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("HeadDetachWatch", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var type = typeof(MyMechanicalConnectionBlockBase);
            if (Constraint == null || TryAttach == null) throw new MissingMemberException("MyMechanicalConnectionBlockBase: Constraint or TryAttach");
            var detach = type.GetMethod("Detach", Any, null, new[] { typeof(MyCubeGrid), typeof(bool) }, null)
                         ?? throw new MissingMethodException(type.Name, "Detach(MyCubeGrid, bool)");
            var safety = type.GetMethod("CheckSafetyDetach", Any, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(type.Name, "CheckSafetyDetach");
            var request = type.GetMethod("DetachRequest", Any, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(type.Name, "DetachRequest");
            var script = type.GetMethod("Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock.Detach", Any, null, Type.EmptyTypes, null)
                         ?? throw new MissingMethodException(type.Name, "IMyMechanicalConnectionBlock.Detach");
            MethodInfo Own(string name) => typeof(HeadDetachWatch).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            ctx.GetPattern(detach).Prefixes.Add(Own(nameof(DetachPrefix)));
            void Mark(MethodInfo method, string prefix)
            {
                var pattern = ctx.GetPattern(method);
                pattern.Prefixes.Add(Own(prefix));
                pattern.Suffixes.Add(Own(nameof(ClearCause)));
            }
            Mark(safety, nameof(SafetyCause));
            Mark(request, nameof(PlayerCause));
            Mark(script, nameof(ScriptCause));
        }

        private static void SafetyCause() => _cause = Cause.Safety;
        private static void PlayerCause() => _cause = Cause.Player;
        private static void ScriptCause() => _cause = Cause.Script;
        private static void ClearCause() => _cause = Cause.Other;

        /// <summary>A head about to come off (its constraint still there): written out, and queued to go back on.</summary>
        private static void DetachPrefix(MyMechanicalConnectionBlockBase __instance, MyCubeGrid topGrid, bool updateGroups)
        {
            try
            {
                var head = __instance.TopBlock;
                // welding, a merge, the world closing: not a head coming off
                if (head == null || !updateGroups || MyEntities.IsClosingAll || !Sync.IsServer) return;
                var cause = _cause;
                System.Threading.Interlocked.Increment(ref Detached);
                if (cause == Cause.Safety) System.Threading.Interlocked.Increment(ref SafetyDetached);
                if (LogOn) Write(__instance, head, topGrid ?? head.CubeGrid, cause);
                if (cause == Cause.Safety && __instance is MyMotorStator stator && !(__instance is MyMotorSuspension) && ReattachOn)
                    Incoming.Enqueue(new Pending { Stator = stator, Head = head });
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "HeadDetachWatch: a head coming off");
            }
        }

        private static void Write(MyMechanicalConnectionBlockBase stator, MyAttachableTopBlockBase head, MyCubeGrid headGrid, Cause cause)
        {
            var grid = stator.CubeGrid;
            string stretch = "?";
            if (Constraint.GetValue(stator) is Havok.HkConstraint constraint && constraint.InWorld)
            {
                constraint.GetPivotsInWorld(out var a, out var b);
                stretch = (a - b).Length().ToString("0.00");
            }
            // a grid taken out of the world (deleted, cleaned up, closed) takes its heads off on the way: not a head coming off
            var removed = !stator.InScene || !head.InScene || grid.MarkedForClose || headGrid.MarkedForClose || stator.MarkedForClose || head.MarkedForClose;
            var what = cause == Cause.Safety ? "torn off by the game (safety detach " + stator.SafetyDetach.ToString("0.#") + " m)" :
                cause == Cause.Player ? "taken off by a player" : cause == Cause.Script ? "taken off by a script" :
                removed ? "off as its grid left the world" : head.SlimBlock?.IsDestroyed == true || stator.SlimBlock?.IsDestroyed == true ? "off as its stator or head was destroyed" : "taken off otherwise";
            var owner = MySession.Static.Players.TryGetIdentity(stator.OwnerId)?.DisplayName ?? stator.OwnerId.ToString();
            var at = stator.PositionComp.GetPosition();
            var nearest = MySession.Static.Players.GetOnlinePlayers().Where(p => p.Character != null && !p.Character.IsDead)
                .Select(p => Vector3D.Distance(p.Character.PositionComp.GetPosition(), at)).DefaultIfEmpty(double.NaN).Min();
            var line = $"Head {what}: {stator.BlockDefinition.Id.SubtypeName} '{stator.CustomName}' on '{grid.DisplayName}' ({grid.EntityId}, owner {owner}), " +
                       $"head on '{headGrid.DisplayName}' ({headGrid.EntityId}, {headGrid.BlocksCount} blocks): joint {stretch} m apart; " +
                       $"stator's grid {Motion(grid)} {Freezer(grid)}, head's grid {Motion(headGrid)} {Freezer(headGrid)}; " +
                       $"nearest player {(double.IsNaN(nearest) ? "none" : nearest.ToString("0") + " m")}, simulation {Sync.ServerSimulationRatio:0.00}";
            if (cause == Cause.Safety || cause == Cause.Other && !removed) SentisOptimisationsPlugin.Log.Warn(line);
            else if (cause == Cause.Other && removed) Leaving.Enqueue(new Left { Line = line, StatorGrid = grid, HeadGrid = headGrid });
            else SentisOptimisationsPlugin.Log.Info(line);
        }

        // A head off as its grid left the world is written a second later and only when one side stayed: a whole
        // mechanism deleted (a cleanup took an NPC base with its rotors and pistons, prod 04.11.2026: a line for each)
        // is only counted. A cleanup closes the grids one by one, so the other side is not marked yet at the detach.
        private sealed class Left
        {
            public string Line;
            public MyCubeGrid StatorGrid, HeadGrid;
            public ulong At;
        }

        private const int LeftWaitFrames = 60;
        private static readonly ConcurrentQueue<Left> Leaving = new ConcurrentQueue<Left>();
        private static readonly List<Left> LeftWaiting = new List<Left>();

        /// <summary>Heads off with their whole mechanism leaving the world (not written to the log).</summary>
        public static long WholeLeft;

        private static bool Gone(MyCubeGrid grid) => grid == null || grid.Closed || grid.MarkedForClose;

        private static void TickLeft(ulong frame)
        {
            while (Leaving.TryDequeue(out var left))
            {
                left.At = frame;
                LeftWaiting.Add(left);
            }
            for (var i = LeftWaiting.Count - 1; i >= 0; i--)
            {
                var left = LeftWaiting[i];
                if (frame - left.At < LeftWaitFrames) continue;
                LeftWaiting.RemoveAt(i);
                if (Gone(left.StatorGrid) && Gone(left.HeadGrid)) WholeLeft++;
                else SentisOptimisationsPlugin.Log.Info(left.Line);
            }
        }

        private static string Motion(MyCubeGrid grid) =>
            grid?.Physics == null ? "(no physics)" : $"{grid.Physics.LinearVelocity.Length():0.0} m/s {grid.Physics.AngularVelocity.Length():0.0} rad/s";

        /// <summary>The freezer's state of the grid: frozen, its body fixed, or how long ago it was thawed.</summary>
        private static string Freezer(MyCubeGrid grid)
        {
            if (grid == null) return "";
            var id = grid.EntityId;
            var physics = FreezeLogic.FrozenPhysicsGrids.Contains(id);
            if (FreezeLogic.FrozenGrids.Contains(id)) return physics ? "(frozen, body fixed)" : "(frozen)";
            if (physics) return "(body fixed by the freezer)";
            if (FreezeLogic.ThawedAtFrame.TryGetValue(id, out var frame))
                return $"(thawed {(MySandboxGame.Static.SimulationFrameCounter - frame) / 60.0:0.0} s ago)";
            return "(not frozen lately)";
        }

        // ------------------------------------------------------------------ putting back (game thread)

        /// <summary>Every frame from the plugin's update, game thread.</summary>
        public static void Tick()
        {
            var frame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            TickLeft(frame);
            while (Incoming.TryDequeue(out var pending))
            {
                pending.Since = frame;
                pending.Next = frame + TryEveryFrames;
                Waiting.Add(pending);
            }
            for (var i = Waiting.Count - 1; i >= 0; i--)
            {
                var p = Waiting[i];
                if (frame < p.Next) continue;
                p.Next = frame + TryEveryFrames;
                string done;
                try
                {
                    done = Try(p, frame);
                }
                catch (Exception e)
                {
                    SentisOptimisationsPlugin.Log.Error(e, "HeadDetachWatch: putting a head back");
                    done = "failed";
                }
                if (done != null) Waiting.RemoveAt(i);
            }
            for (var i = Verifying.Count - 1; i >= 0; i--)
            {
                var v = Verifying[i];
                if (frame < v.At) continue;
                Verifying.RemoveAt(i);
                if (v.Stator.MarkedForClose || v.Head.MarkedForClose) continue;
                if (v.Stator.TopBlock == v.Head) continue;
                System.Threading.Interlocked.Increment(ref OffAgain);
                SentisOptimisationsPlugin.Log.Warn($"HeadDetachWatch: the head put back on '{v.Stator.CustomName}' on '{v.Stator.CubeGrid.DisplayName}' came off again within {VerifyFrames} frames");
            }
        }

        /// <summary>One try: null - try again later; else why it is done (put back, or given up).</summary>
        private static string Try(Pending p, ulong frame)
        {
            var stator = p.Stator;
            var head = p.Head;
            var name = $"{stator.BlockDefinition.Id.SubtypeName} '{stator.CustomName}' on '{stator.CubeGrid?.DisplayName}'";
            if (!ReattachOn) return "off";
            if (stator.MarkedForClose || stator.Closed || head.MarkedForClose || head.Closed || head.SlimBlock?.IsDestroyed == true)
                return GiveUp(name, "the stator or the head is gone");
            if (stator.TopBlock != null) return "the stator has a head again";
            if (head.Stator != null) return "the head is on a stator again";
            if (!stator.IsFunctional) return GiveUp(name, "the stator is broken");
            if (frame - p.Since > GiveUpFrames) return GiveUp(name, p.Waiting ?? "time");

            var reason = Blocked(stator, head, out var move, out var group);
            if (reason != null)
            {
                if (p.Waiting != reason)
                {
                    p.Waiting = reason;
                    SentisOptimisationsPlugin.Log.Info($"HeadDetachWatch: the head of {name} waits to go back: {reason}");
                }
                return null;
            }
            var moved = (Vector3D.Transform(Vector3D.Zero, MatrixD.CreateTranslation(head.CubeGrid.PositionComp.GetPosition()) * move) - head.CubeGrid.PositionComp.GetPosition()).Length();
            if (!(bool)TryAttach.Invoke(stator, new object[] { head, true })) return null;
            MechanicalTopCheck.ResendProperties(stator);
            System.Threading.Interlocked.Increment(ref PutBack);
            Verifying.Add(new Verify { Stator = stator, Head = head, At = frame + VerifyFrames });
            SentisOptimisationsPlugin.Log.Warn($"HeadDetachWatch: the head of {name} put back on, its group of {group} grids moved {moved:0.00} m, " +
                                                                        $"{(frame - p.Since) / 60.0:0.0} s after it came off");
            return "put back";
        }

        private static string GiveUp(string name, string why)
        {
            System.Threading.Interlocked.Increment(ref GivenUp);
            SentisOptimisationsPlugin.Log.Warn($"HeadDetachWatch: the head of {name} not put back: {why}");
            return "given up";
        }

        /// <summary>Why the head cannot go back on now, or null; the move the game would make and the grids it would move.</summary>
        internal static string Blocked(MyMotorStator stator, MyAttachableTopBlockBase head, out MatrixD move, out int count)
        {
            move = MatrixD.Identity;
            count = 0;
            var grid = stator.CubeGrid;
            var headGrid = head.CubeGrid;
            if (grid?.Physics?.RigidBody == null || headGrid?.Physics?.RigidBody == null) return "no physics";
            if (!grid.Physics.RigidBody.InWorld || !headGrid.Physics.RigidBody.InWorld || grid.Physics.HavokWorld != headGrid.Physics.HavokWorld) return "not in one physics world";
            if (MyCubeGridGroups.Static.Physical.HasSameGroup(grid, headGrid)) return "the head's grid is held to the stator's another way";
            var group = MyCubeGridGroups.Static.Physical.GetGroup(headGrid)?.Nodes.Select(n => n.NodeData).ToList() ?? new List<MyCubeGrid> { headGrid };
            count = group.Count;
            if (group.Any(g => g.IsStatic || g.Physics?.RigidBody == null || g.Physics.RigidBody.IsFixedOrKeyframed))
                return "the head's group is anchored (the game would move the stator's ship)";

            // the game's AlignGridsForAttach: the head's pivot onto the stator's, the head's up onto the stator's axis
            var pivot = Vector3D.Transform(stator.DummyPosition, grid.WorldMatrix);
            var headPivot = Vector3D.Transform(head.Position * headGrid.GridSize, headGrid.WorldMatrix);
            var distance = Vector3D.Distance(pivot, headPivot);
            if (distance > MaxDistance) return $"the head is {distance:0.0} m off (more than {MaxDistance:0.#})";
            var relative = (headGrid.Physics.LinearVelocity - grid.Physics.LinearVelocity).Length();
            var turn = (headGrid.Physics.AngularVelocity - grid.Physics.AngularVelocity).Length();
            if (relative > MaxRelativeSpeed || turn > MaxRelativeTurn) return $"the head moves against the stator at {relative:0.0} m/s {turn:0.0} rad/s";
            var up = stator.WorldMatrix.Up;
            var headUp = head.WorldMatrix.Up;
            var axis = Vector3D.Dot(up, headUp) < -0.95 ? -up : up;
            if (Vector3D.Dot(headUp, axis) > 0.99996)
                move = MatrixD.CreateTranslation(pivot - headPivot);
            else
            {
                QuaternionD.CreateFromTwoVectors(ref headUp, ref axis, out var turnTo);
                move = MatrixD.CreateTranslation(-headPivot) * MatrixD.CreateFromQuaternion(turnTo) * MatrixD.CreateTranslation(pivot);
            }
            return InTheWay(group, grid, headGrid, move);
        }

        private static readonly List<MyEntity> Near = new List<MyEntity>();

        /// <summary>What any grid of the group would hit where the move puts it, or null.</summary>
        internal static string InTheWay(List<MyCubeGrid> group, MyCubeGrid statorGrid, MyCubeGrid headGrid, MatrixD move)
        {
            foreach (var g in group)
            {
                if (g.Physics?.RigidBody == null) continue;
                var body = g.Physics.RigidBody;
                var landing = g.WorldMatrix * move;
                var local = new BoundingBoxD(g.PositionComp.LocalAABB.Min, g.PositionComp.LocalAABB.Max);
                var box = local.TransformFast(ref landing);
                box.Inflate(0.5);
                // the body's own frame (its centre of mass), moved
                var bodyMatrix = (MatrixD)body.GetRigidBodyMatrix();
                bodyMatrix.Translation = g.Physics.ClusterToWorld(body.Position);
                var bodyLanding = bodyMatrix * move;
                var position = bodyLanding.Translation;
                var rotation = Quaternion.CreateFromRotationMatrix(bodyLanding);
                Near.Clear();
                MyGamePruningStructure.GetTopMostEntitiesInBox(ref box, Near);
                try
                {
                    foreach (var other in Near)
                    {
                        if (other is MyCubeGrid h)
                        {
                            if (group.Contains(h) || h.Physics?.RigidBody == null || h.MarkedForClose) continue;
                            // the stator's grid and the head's own never collide
                            if (h == statorGrid && g == headGrid) continue;
                            var hb = h.Physics.RigidBody;
                            var hp = h.Physics.ClusterToWorld(hb.Position);
                            var hr = Quaternion.CreateFromRotationMatrix(hb.GetRigidBodyMatrix());
                            if (MyPhysics.IsPenetratingShapeShape(body.GetShape(), ref position, ref rotation, hb.GetShape(), ref hp, ref hr))
                                return $"'{g.DisplayName}' would land in '{h.DisplayName}'";
                        }
                        else if (other is MyVoxelBase voxel)
                        {
                            if (voxel.IsAnyAabbCornerInside(ref landing, local)) return $"'{g.DisplayName}' would land in the rock";
                        }
                        else if (other is MyCharacter character && !character.IsDead)
                        {
                            var inside = Vector3D.Transform(character.PositionComp.GetPosition(), MatrixD.Invert(landing));
                            var grown = local;
                            grown.Inflate(0.5);
                            if (grown.Contains(inside) != ContainmentType.Disjoint)
                                return $"'{g.DisplayName}' would land on {character.DisplayName}";
                        }
                    }
                }
                finally
                {
                    Near.Clear();
                }
            }
            return null;
        }
    }
}
