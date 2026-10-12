using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// A piston's head that cannot be checked is not placed, instead of the request throwing.
    ///
    /// "Add head" of a piston (<c>DoRecreateTop</c>, a request from the player's client) checks the place of the new head
    /// in <c>MyPistonBase.CanPlaceTop</c>, which reads the piston's third subpart, the head's collision shapes and the
    /// physics of whatever the head's shape touches - and threw a NullReferenceException out of the network event on the
    /// server (11.10.2026, the player was dropped right after). Here such a throw means "the head does not fit": no head,
    /// the half-made head grid closed by the game as for any head that does not fit, and a line in the log with what was
    /// missing, at most every 10 s. The same for any throw while a head of a rotor, a hinge or a piston is made
    /// (<c>CreateTopPart</c>: a piston's <c>GetTopGridMatrix</c> reads the same subpart) - no head, and the head grid made
    /// so far closed.
    /// </summary>
    [PatchShim]
    public static class PistonTopPlacement
    {
        private static DateTime _logged;

        /// <summary>Piston heads not placed for a throw in the check, since the start.</summary>
        public static long Refused;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("PistonTopPlacement", ctx, c =>
        {
            var check = typeof(MyPistonBase).GetMethod("CanPlaceTop", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                            new[] { typeof(MyAttachableTopBlockBase), typeof(long) }, null)
                        ?? throw new MissingMethodException("MyPistonBase.CanPlaceTop(MyAttachableTopBlockBase, long)");
            CrashFixPatch.harmony.Patch(check, finalizer: new HarmonyMethod(typeof(PistonTopPlacement).GetMethod(nameof(CanPlaceTopFinalizer),
                BindingFlags.Static | BindingFlags.NonPublic)));
            // and the making of any head (a piston's GetTopGridMatrix reads the same subpart): a throw there is no head
            var make = typeof(MyMechanicalConnectionBlockBase).GetMethod("CreateTopPart", BindingFlags.Instance | BindingFlags.NonPublic)
                       ?? throw new MissingMethodException("MyMechanicalConnectionBlockBase.CreateTopPart");
            CrashFixPatch.harmony.Patch(make, finalizer: new HarmonyMethod(typeof(PistonTopPlacement).GetMethod(nameof(CreateTopPartFinalizer),
                BindingFlags.Static | BindingFlags.NonPublic)));
        });

        private static Exception CreateTopPartFinalizer(Exception __exception, MyMechanicalConnectionBlockBase __instance, ref MyAttachableTopBlockBase topBlock)
        {
            if (__exception == null) return null;
            try
            {
                // a head grid made and not added: closed, as the game closes one that does not fit
                if (topBlock?.CubeGrid != null && !topBlock.CubeGrid.InScene) topBlock.CubeGrid.Close();
            }
            catch
            {
                // nothing more to put right
            }
            topBlock = null;
            Refused++;
            Write(__exception, $"the head of '{__instance?.CustomName}' ({__instance?.BlockDefinition?.Id.SubtypeName}) on {__instance?.CubeGrid?.DisplayName} not made: making it threw " +
                               $"(built {__instance?.SlimBlock?.BuildLevelRatio:0.00})");
            return null;
        }

        private static void Write(Exception e, string what)
        {
            try
            {
                var now = DateTime.UtcNow;
                if ((now - _logged).TotalSeconds < 10) return;
                _logged = now;
                SentisOptimisationsPlugin.Log.Warn(e, "PistonTopPlacement: " + what);
            }
            catch
            {
                // the log must not throw from a finalizer
            }
        }

        private static readonly MethodInfo AddShapesForPistonTop = typeof(MyPistonBase).Assembly
            .GetType("Sandbox.Game.Entities.Cube.MyCubeBlockCollector")?.GetMethod("AddShapesForPistonTop", BindingFlags.Static | BindingFlags.Public);

        /// <summary>
        /// The bodies at the head's place, as <c>MyPistonBase.CanPlaceTop</c> looks for them, and which of them have no entity or
        /// an entity without physics (the check reads the physics of each). Only when the check threw.
        /// </summary>
        private static string Touching(MyPistonBase piston, MyAttachableTopBlockBase top)
        {
            try
            {
                var subpart = piston?.Subpart3;
                var shapes = top?.ModelCollision?.HavokCollisionShapes;
                if (subpart?.Model == null || shapes == null || AddShapesForPistonTop == null) return "not looked";
                Vector3D at = subpart.WorldMatrix.Translation + piston.WorldMatrix.Up * subpart.Model.BoundingBoxSize.Y;
                if (subpart.Model.Dummies.TryGetValue("TopBlock", out var dummy)) at = ((MatrixD)dummy.Matrix * subpart.WorldMatrix).Translation;
                var list = new List<HkShape>();
                AddShapesForPistonTop.Invoke(null, new object[] { top.SlimBlock, shapes, list });
                if (list.Count == 0) return "the head has no shape";
                var rotation = Quaternion.CreateFromForwardUp(piston.WorldMatrix.Forward, piston.WorldMatrix.Up);
                var hits = new List<HkBodyCollision>();
                MyPhysics.GetPenetrationsShape(list[0], ref at, ref rotation, hits, 15);
                var odd = hits.Select(h => h.GetCollisionEntity()).Select(e => e == null ? "a body without an entity" : e.Physics == null ? $"{e.GetType().Name} '{e.DisplayName}' without physics" : null)
                    .Where(s => s != null).ToList();
                return $"{hits.Count} bodies" + (odd.Count == 0 ? "" : ": " + string.Join(", ", odd));
            }
            catch (Exception e)
            {
                return "looking threw " + e.GetType().Name;
            }
        }

        private static Exception CanPlaceTopFinalizer(Exception __exception, MyPistonBase __instance, MyAttachableTopBlockBase topBlock, ref bool __result)
        {
            if (__exception == null) return null;
            __result = false;
            Refused++;
            try
            {
                var now = DateTime.UtcNow;
                if ((now - _logged).TotalSeconds >= 10)
                {
                    _logged = now;
                    var subpart = __instance?.Subpart3;
                    SentisOptimisationsPlugin.Log.Warn(__exception,
                        $"PistonTopPlacement: the head of piston '{__instance?.CustomName}' ({__instance?.BlockDefinition?.Id.SubtypeName}) on {__instance?.CubeGrid?.DisplayName} " +
                        $"not placed: its check threw (third subpart {(subpart == null ? "missing" : subpart.Model == null ? "without a model" : "there")}, " +
                        $"head {(topBlock == null ? "missing" : topBlock.Model == null ? "without a model" : topBlock.BlockDefinition?.Id.SubtypeName)}, " +
                        $"head collision {(topBlock?.ModelCollision == null ? "missing" : topBlock.ModelCollision.HavokCollisionShapes == null ? "without shapes" : topBlock.ModelCollision.HavokCollisionShapes.Length + " shapes")}, " +
                        $"piston built {__instance?.SlimBlock?.BuildLevelRatio:0.00}, in scene {__instance?.InScene}, grid physics {(__instance?.CubeGrid?.Physics == null ? "none" : "there")}; " +
                        $"what the head's place touches: {Touching(__instance, topBlock)})");
                }
            }
            catch
            {
                // the log must not throw from a finalizer
            }
            return null;
        }
    }
}
