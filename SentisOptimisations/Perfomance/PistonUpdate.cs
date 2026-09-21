using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Torch.Managers.PatchManager.MSIL;
using VRageMath;
using Sandbox.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using Torch.Utils;
using VRage.Game.Entity;
using VRage.ModAPI;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// What a piston does every frame, without the parts that cannot change anything.
    ///
    /// Every working piston runs MyPistonBase.UpdatePosition every frame, and two kinds of piston
    /// run it for nothing:
    ///
    ///  * one resting against the limit it is driving towards - it reads its constraint impulses
    ///    from Havok (six native calls), looks up its top grid, and throws the answer away;
    ///  * one with its velocity at exactly zero somewhere between its limits - the game takes that
    ///    for moving by nothing, and every frame wakes the rigid bodies on both ends, drops the
    ///    group's mass cache, recomputes the animation and the constraint data. Its grids never
    ///    get to sleep in Havok.
    ///
    /// Neither piston changes anything in those frames: the game ends up doing exactly what is
    /// done here - drop the 10th-frame update and stop the moving sound. A frame in which the
    /// velocity changes sign is left to the game, which notes it.
    ///
    /// A piston at rest also holds what it carries rigidly, and so does every resting piston below
    /// it: down to the first joint that really moves - a piston on its way, a rotor, a hinge - or
    /// to the grid the stack stands on, the whole stack is one rigid body in all but Havok's eyes.
    /// Any motion of a carried grid against that anchor is sway: a stack put up by hand, or
    /// knocked, swings for a minute, and every grid in it stays awake in Havok while it does. Part
    /// of that motion is taken away every frame - a stack standing on a moving ship keeps moving
    /// with the ship - and below a small threshold nothing is touched at all, so Havok can put the
    /// stack to sleep. (Damping each grid against the one right below it does nothing: a swaying
    /// stack bends as a whole, and neighbours hardly move against each other.)
    ///
    /// A piston on the move asks, every frame, whether a safe zone forbids it, and to ask it
    /// computes the bounding box of its whole physical group: every piston of a stack of fifteen
    /// grids computes the same box. While pistons update nothing moves, so within a frame the box
    /// is computed once per group.
    /// </summary>
    [PatchShim]
    public static class PistonUpdate
    {
        [ReflectedGetter(Name = "m_currentPos")]
        private static Func<MyPistonBase, float> _currentPos;

        [ReflectedGetter(Name = "m_lastVelocity")]
        private static Func<MyPistonBase, float> _lastVelocity;

        [ReflectedGetter(Name = "m_subpart1")]
        private static Func<MyPistonBase, MyEntitySubpart> _subpart1;

        [ReflectedGetter(Name = "m_subpartPhysics")]
        private static Func<MyPistonBase, MyPhysicsBody> _subpartPhysics;

        /// <summary>
        /// Sway is looked at every <see cref="DampEveryFrames"/> frames per piston, each on its own
        /// frame: asking Havok whether a body is awake is the expensive part, and a sway lasts far
        /// longer than that. Each look takes this share of the motion away - about what a tenth a
        /// frame would.
        /// </summary>
        public const int DampEveryFrames = 4;
        public const float DampingPerLook = 0.35f;

        /// <summary>Below these the motion is left alone, so the bodies can fall asleep.</summary>
        public const float CalmLinear = 0.02f;
        public const float CalmAngular = 0.005f;

        public static long Damped;

        private static int _boxesFrame = -1;
        private static readonly Dictionary<MyCubeGrid, BoundingBoxD> Boxes = new Dictionary<MyCubeGrid, BoundingBoxD>();
        private static readonly MethodInfo GroupBox = typeof(MyCubeGrid).GetMethod(nameof(MyCubeGrid.GetPhysicalGroupAABB),
            BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);

        private static Action<MyPistonBase> _stopMovingSound;

        public static long Skipped;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("PistonUpdate", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var update = typeof(MyPistonBase).GetMethod("UpdatePosition", any, null, new[] { typeof(bool) }, null)
                         ?? throw new MissingMethodException("MyPistonBase.UpdatePosition(bool)");
            var stopSound = typeof(MyPistonBase).GetMethod("StopMovingSound", any, null, Type.EmptyTypes, null)
                            ?? throw new MissingMethodException("MyPistonBase.StopMovingSound()");
            _stopMovingSound = (Action<MyPistonBase>)Delegate.CreateDelegate(typeof(Action<MyPistonBase>), stopSound);

            if (GroupBox == null) throw new MissingMethodException("MyCubeGrid.GetPhysicalGroupAABB()");
            var pattern = ctx.GetPattern(update);
            pattern.Prefixes.Add(Method(nameof(UpdatePositionPrefix)));
            pattern.Transpilers.Add(Method(nameof(Transpiler)));
        }

        private static MethodInfo Method(string name) =>
            typeof(PistonUpdate).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        /// <summary>
        /// Whether this frame's update would change nothing: the piston rests against the limit
        /// it drives towards, or stands at velocity zero, and the direction has not just flipped.
        /// </summary>
        public static bool AtRest(float velocity, float lastVelocity, float position, float min, float max)
        {
            if (Math.Sign(lastVelocity) != Math.Sign(velocity)) return false;
            if (velocity == 0f) return true;
            return velocity < 0f ? position <= min : position >= max;
        }

        private static bool UpdatePositionPrefix(MyPistonBase __instance, bool forceUpdate)
        {
            try
            {
                if (forceUpdate || _subpart1(__instance) == null || !__instance.IsWorking) return true;
                if (!AtRest(__instance.Velocity, _lastVelocity(__instance), _currentPos(__instance),
                        __instance.MinLimit, __instance.MaxLimit))
                    return true;

                if (((MySession.Static.GameplayFrameCounter + (int)__instance.EntityId) & (DampEveryFrames - 1)) == 0)
                    DampWobble(__instance);
                _stopMovingSound(__instance);
                if ((__instance.NeedsUpdate & MyEntityUpdateEnum.EACH_10TH_FRAME) != 0)
                    __instance.NeedsUpdate &= ~MyEntityUpdateEnum.EACH_10TH_FRAME;
                Skipped++;
                return false;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "piston update shortcut failed");
                return true;
            }
        }


        /// <summary>
        /// Takes <see cref="DampingPerLook"/> of the carried grid's motion against the piston's
        /// grid away - and of the piston head's, which sits between the two.
        /// </summary>
        private static void DampWobble(MyPistonBase piston)
        {
            // the head never sways without the grid it carries: one check is enough to leave a
            // sleeping stack alone, and it is made for every resting piston every frame
            var top = piston.TopGrid?.Physics;
            if (!Awake(top)) return;
            var head = _subpartPhysics(piston);
            var anchor = Anchor(piston.CubeGrid)?.Physics;
            if (anchor == null || !anchor.Enabled) return;
            if (Damp(top, anchor)) Damped++;
            Damp(head, anchor);
        }

        private static bool Awake(MyPhysicsBody body) =>
            body != null && body.Enabled && !body.IsStatic && body.RigidBody != null && body.RigidBody.IsActive;

        /// <summary>
        /// The grid this one is rigidly part of: down the physical hierarchy for as long as the
        /// joint to the next grid is a piston that is not moving.
        /// </summary>
        private static MyCubeGrid Anchor(MyCubeGrid grid)
        {
            var hierarchy = MyGridPhysicalHierarchy.Static;
            if (grid == null || hierarchy == null) return grid;
            for (var steps = 0; steps < 64; steps++)
            {
                var parent = hierarchy.GetParent(grid);
                if (parent == null) break;
                var piston = hierarchy.GetEntityConnectingToParent(grid) as MyPistonBase;
                if (piston == null || !Rigid(piston) ||
                    !(piston.TopGrid == grid && piston.CubeGrid == parent || piston.CubeGrid == grid && piston.TopGrid == parent))
                    break;
                grid = parent;
            }
            return grid;
        }

        private static bool Rigid(MyPistonBase piston) =>
            !piston.IsWorking || AtRest(piston.Velocity, _lastVelocity(piston), _currentPos(piston), piston.MinLimit, piston.MaxLimit);

        private static bool Damp(MyPhysicsBody body, MyPhysicsBody anchor)
        {
            if (body == null || !body.Enabled || body.IsStatic || body.RigidBody == null || !body.RigidBody.IsActive)
                return false;
            var linear = body.LinearVelocity;
            var angular = body.AngularVelocity;
            Vector3 anchorLinear, anchorAngular;
            if (anchor.IsStatic || anchor.RigidBody == null)
            {
                anchorLinear = Vector3.Zero;
                anchorAngular = Vector3.Zero;
            }
            else
            {
                anchorLinear = anchor.GetVelocityAtPoint(body.CenterOfMassWorld);
                anchorAngular = anchor.AngularVelocity;
            }

            var relativeLinear = linear - anchorLinear;
            var relativeAngular = angular - anchorAngular;
            if (relativeLinear.LengthSquared() < CalmLinear * CalmLinear &&
                relativeAngular.LengthSquared() < CalmAngular * CalmAngular)
                return false;

            body.LinearVelocity = linear - relativeLinear * DampingPerLook;
            body.AngularVelocity = angular - relativeAngular * DampingPerLook;
            return true;
        }

        /// <summary>UpdatePosition asks for its group's box through <see cref="GroupBoxThisFrame"/>.</summary>
        internal static IEnumerable<MsilInstruction> Transpiler(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            var replaced = 0;
            for (var i = 0; i < list.Count; i++)
            {
                var instruction = list[i];
                if ((instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) &&
                    instruction.Operand is MsilOperandInline<MethodBase> operand && operand.Value == GroupBox)
                {
                    list[i] = new MsilInstruction(OpCodes.Call).InlineValue(
                        (MethodBase)typeof(PistonUpdate).GetMethod(nameof(GroupBoxThisFrame), BindingFlags.Static | BindingFlags.Public));
                    replaced++;
                }
            }
            if (replaced == 0)
                throw new InvalidOperationException("PistonUpdate: no GetPhysicalGroupAABB call in MyPistonBase.UpdatePosition");
            return list;
        }

        /// <summary>
        /// The group's box, computed once per frame per grid. Only on the game thread: anywhere
        /// else it is computed as the game would.
        /// </summary>
        public static BoundingBoxD GroupBoxThisFrame(MyCubeGrid grid)
        {
            if (grid == null || MySandboxGame.Static == null ||
                Thread.CurrentThread != MySandboxGame.Static.UpdateThread || MySession.Static == null)
                return grid.GetPhysicalGroupAABB();
            var frame = MySession.Static.GameplayFrameCounter;
            if (frame != _boxesFrame)
            {
                _boxesFrame = frame;
                Boxes.Clear();
            }
            BoundingBoxD box;
            if (!Boxes.TryGetValue(grid, out box))
            {
                box = grid.GetPhysicalGroupAABB();
                Boxes[grid] = box;
            }
            return box;
        }
    }
}
