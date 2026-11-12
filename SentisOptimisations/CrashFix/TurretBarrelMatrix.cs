using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Sandbox.Game.Weapons;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// A turret without its barrel gives its own block's matrix instead of taking the server down.
    ///
    /// <c>MyLargeTurretBase.GetWorldMatrix()</c> is <c>Barrel.Entity.WorldMatrix</c>, and a turret can be without its
    /// barrel: a closed one (<c>OnClose</c> closes it and sets it to null) and one ground or damaged below its built state,
    /// which stays in the world (<c>OnModelChange</c> of the gatling, missile and interior turrets makes the barrel only
    /// while the block <c>IsBuilt</c>). A character's target lock keeps the turret it locks from
    /// (<c>MyTargetLockingComponent.m_controlledBlock</c>) until its controlled entity changes, and the lock's check of
    /// the target's visibility on the server (<c>UpdateBeforeSimulation</c> → <c>CanTarget</c>) asked such a turret for
    /// its matrix: a NullReferenceException in the session components' update, which no finalizer covers, and the server
    /// went down (08.11.2026 17:18, production). <c>MyTargetFocusComponent.UpdateAimVector</c> asks the same. The block's
    /// own matrix is where the barrel stands, near enough for whoever asks; the lock goes as the game lets go of it.
    ///
    /// Patched with Harmony, not Torch: the method returns a <c>MatrixD</c> (128 bytes) through a hidden buffer, and
    /// Torch's re-emitted copy of such an instance method takes the instance and the buffer the other way round - it
    /// would write the matrix over the turret and take the caller's stack for the turret (AGENTS.md, patch_retbuf).
    /// Such a turret is written to the log once a minute, with its closed, marked and built flags.
    /// </summary>
    [PatchShim]
    public static class TurretBarrelMatrix
    {
        private static readonly Harmony Harmony = new Harmony("SentisOptimisations.TurretBarrelMatrix");
        private static long _loggedAtTicks = long.MinValue / 2;

        /// <summary>For the stand's tests: the matrices given for a turret without its barrel, since the start.</summary>
        public static long Given;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("TurretBarrelMatrix", ctx, _ =>
        {
            var method = typeof(MyLargeTurretBase).GetMethod(nameof(MyLargeTurretBase.GetWorldMatrix), BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)
                         ?? throw new MissingMethodException(nameof(MyLargeTurretBase), nameof(MyLargeTurretBase.GetWorldMatrix));
            Harmony.Patch(method, prefix: new HarmonyMethod(typeof(TurretBarrelMatrix).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)));
        });

        private static bool Prefix(MyLargeTurretBase __instance, ref MatrixD __result)
        {
            if (__instance.Barrel?.Entity != null) return true;
            __result = __instance.WorldMatrix;
            Interlocked.Increment(ref Given);
            try
            {
                // turrets update on SentisClusters' threads too: one line a minute, whichever thread
                var now = DateTime.UtcNow.Ticks;
                var last = Interlocked.Read(ref _loggedAtTicks);
                if (now - last >= TimeSpan.TicksPerMinute && Interlocked.CompareExchange(ref _loggedAtTicks, now, last) == last)
                    SentisOptimisationsPlugin.Log.Warn("Turret " + __instance.EntityId + " '" + __instance.CustomName + "' on '" + __instance.CubeGrid?.DisplayName +
                                                       "' (closed " + __instance.Closed + ", marked " + __instance.MarkedForClose + ", built " + __instance.IsBuilt +
                                                       ") asked for its matrix without its barrel; gave the block's own");
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "TurretBarrelMatrix");
            }
            return false;
        }
    }
}
