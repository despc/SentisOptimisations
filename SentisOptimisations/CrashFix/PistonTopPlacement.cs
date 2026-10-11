using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities.Blocks;
using Torch.Managers.PatchManager;

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
                        $"PistonTopPlacement: the head of piston '{__instance?.CustomName}' on {__instance?.CubeGrid?.DisplayName} not placed: its check threw " +
                        $"(third subpart {(subpart == null ? "missing" : subpart.Model == null ? "without a model" : "there")}, " +
                        $"head collision {(topBlock?.ModelCollision?.HavokCollisionShapes == null ? "missing" : topBlock.ModelCollision.HavokCollisionShapes.Length + " shapes")}, " +
                        $"piston built {__instance?.SlimBlock?.BuildLevelRatio:0.00})");
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
