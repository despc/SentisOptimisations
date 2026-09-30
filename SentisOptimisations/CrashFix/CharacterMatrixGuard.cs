using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sandbox.Game.Entities.Character;
using Torch.Managers.PatchManager;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRageMath;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// A character's box never grows past the character.
    ///
    /// An entity's box is its model's box put through its world matrix. A character whose matrix had axes three billion
    /// times too long (and a speed of 10^21 m/s) got a box billions of kilometres across: the planets built their voxel
    /// physics around it and the stand hung (28-29.09.2026, a SentisAi bot, the same numbers after every load of the
    /// world). <see cref="PlanetPhysicsBox"/> keeps such a box out of the pruning tree; here it is not made at all.
    ///
    /// Every change of a character's world matrix ends in <c>MyCharacterPosition.OnWorldPositionChanged</c>, before the
    /// box is worked out from it. There a matrix whose axes are not unit is straightened, a place that is not a number
    /// or farther than <see cref="MaxCoordinateM"/> goes back to the last sane one, and a speed past
    /// <see cref="MaxSpeed"/> is stopped. What was put right, and who changed the matrix, is written to the log once a
    /// minute: the way to the cause.
    /// </summary>
    [PatchShim]
    public static class CharacterMatrixGuard
    {
        /// <summary>No character is farther from the centre of the world than this: a million kilometres.</summary>
        public const double MaxCoordinateM = 1e9;

        /// <summary>No character moves faster than this, jetpack, ship or planet's pull (m/s).</summary>
        public const float MaxSpeed = 10000;

        private static readonly ConditionalWeakTable<object, StrongBox<Vector3D>> LastSane = new ConditionalWeakTable<object, StrongBox<Vector3D>>();
        private static DateTime _loggedAt = DateTime.MinValue;
        private static int _fixes;

        // the component whose matrix the prefix put right, for the suffix to give the physics the same
        [ThreadStatic] private static object _corrected;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("CharacterMatrixGuard", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var type = typeof(MyCharacter).Assembly.GetType("Sandbox.Game.Entities.Character.Components.MyCharacterPosition", true);
            var method = type.GetMethod("OnWorldPositionChanged", BindingFlags.Instance | BindingFlags.NonPublic, null,
                             new[] { typeof(object), typeof(bool), typeof(bool) }, null)
                         ?? throw new MissingMethodException(type.Name, "OnWorldPositionChanged");
            ctx.GetPattern(method).Prefixes.Add(typeof(CharacterMatrixGuard).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
            ctx.GetPattern(method).Suffixes.Add(typeof(CharacterMatrixGuard).GetMethod(nameof(Suffix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>An axis of the rotation not of unit length (by a tenth or more), or not a number.</summary>
        public static bool Scaled(ref MatrixD m) =>
            !(Unit(m.M11, m.M12, m.M13) && Unit(m.M21, m.M22, m.M23) && Unit(m.M31, m.M32, m.M33));

        private static bool Unit(double x, double y, double z)
        {
            var squared = x * x + y * y + z * z;
            return squared > 0.81 && squared < 1.21;
        }

        /// <summary>A place a character can be at.</summary>
        public static bool SanePlace(Vector3D at) => at.IsValid() && Math.Abs(at.X) < MaxCoordinateM && Math.Abs(at.Y) < MaxCoordinateM && Math.Abs(at.Z) < MaxCoordinateM;

        private static void Prefix(object __instance, ref MatrixD __field_m_worldMatrix)
        {
            try
            {
                string fixedWhat = null;
                if (Scaled(ref __field_m_worldMatrix))
                {
                    __field_m_worldMatrix = PlanetPhysicsBox.Straightened(__field_m_worldMatrix);
                    fixedWhat = "a scaled matrix straightened";
                }

                var last = LastSane.GetValue(__instance, _ => new StrongBox<Vector3D>(Vector3D.Zero));
                var at = __field_m_worldMatrix.Translation;
                if (SanePlace(at)) last.Value = at;
                else
                {
                    __field_m_worldMatrix.Translation = last.Value;
                    fixedWhat = (fixedWhat == null ? "" : fixedWhat + ", ") + "a place " + at + " put back to " + last.Value;
                }

                var entity = (__instance as MyEntityComponentBase)?.Entity as MyEntity;
                var physics = entity?.Physics;
                if (physics != null)
                {
                    var speed = physics.LinearVelocity.Length();
                    if (!(speed <= MaxSpeed))
                    {
                        physics.LinearVelocity = Vector3.Zero;
                        physics.AngularVelocity = Vector3.Zero;
                        fixedWhat = (fixedWhat == null ? "" : fixedWhat + ", ") + "a speed of " + speed + " m/s stopped";
                    }
                }

                if (fixedWhat == null) return;
                _corrected = __instance;
                _fixes++;
                if ((DateTime.UtcNow - _loggedAt).TotalSeconds < 60) return;
                _loggedAt = DateTime.UtcNow;
                SentisOptimisationsPlugin.Log.Error("CharacterMatrixGuard: " + (entity?.DisplayName ?? "?") + " (" + entity?.EntityId + "): " + fixedWhat +
                                                    " (" + _fixes + " put right since the last report)\n" + Environment.StackTrace);
                _fixes = 0;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "CharacterMatrixGuard");
            }
        }

        /// <summary>
        /// The physics given the matrix put right: the character's Havok proxy keeps a forward and an up of its own, and
        /// every physics step builds the matrix from them (<c>MyCharacter.UpdatePhysicalMovement</c>) - left as they
        /// were, the next step brought the same broken matrix back.
        /// </summary>
        private static void Suffix(object __instance, ref MatrixD __field_m_worldMatrix)
        {
            if (!ReferenceEquals(_corrected, __instance)) return;
            _corrected = null;
            try
            {
                var physics = ((__instance as MyEntityComponentBase)?.Entity as MyEntity)?.Physics as Sandbox.Engine.Physics.MyPhysicsBody;
                if (physics == null) return;
                physics.CharacterProxy?.SetForwardAndUp(__field_m_worldMatrix.Forward, __field_m_worldMatrix.Up);
                physics.OnWorldPositionChanged(null);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "CharacterMatrixGuard");
            }
        }
    }
}
