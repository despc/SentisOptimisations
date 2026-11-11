using System;
using System.Reflection;
using Sandbox.Game.Weapons;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// A turret without its barrel gives its own block's matrix instead of taking the server down.
    ///
    /// <c>MyLargeTurretBase.GetWorldMatrix()</c> is <c>Barrel.Entity.WorldMatrix</c>, and a closed turret has no barrel
    /// (<c>OnClose</c> closes it and sets it to null). A character's target lock keeps the turret it locked from
    /// (<c>MyTargetLockingComponent.m_controlledBlock</c>) until its controlled entity changes; the turret closed before
    /// that, and the lock's check of the target's visibility on the server (<c>UpdateBeforeSimulation</c> →
    /// <c>CanTarget</c>) asked it for its matrix: a NullReferenceException in the session components' update, which no
    /// finalizer covers, and the server went down (08.11.2026 17:18, production, a player left 1.3 s before). The
    /// block's own matrix is where the barrel stands, near enough for whoever asks; the lock goes as the game lets go of
    /// it.
    /// </summary>
    [PatchShim]
    public static class TurretBarrelMatrix
    {
        private static DateTime _loggedAt = DateTime.MinValue;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("TurretBarrelMatrix", ctx, c =>
        {
            var method = typeof(MyLargeTurretBase).GetMethod(nameof(MyLargeTurretBase.GetWorldMatrix),
                             BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)
                         ?? throw new MissingMethodException(nameof(MyLargeTurretBase), nameof(MyLargeTurretBase.GetWorldMatrix));
            c.GetPattern(method).Prefixes.Add(typeof(TurretBarrelMatrix).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        });

        private static bool Prefix(MyLargeTurretBase __instance, ref MatrixD __result)
        {
            if (__instance.Barrel?.Entity != null) return true;
            __result = __instance.WorldMatrix;
            if ((DateTime.UtcNow - _loggedAt).TotalSeconds >= 60)
            {
                _loggedAt = DateTime.UtcNow;
                SentisOptimisationsPlugin.Log.Warn("Turret " + __instance.EntityId + " '" + __instance.CustomName + "' on '" + __instance.CubeGrid?.DisplayName +
                                                   "' (closed " + __instance.Closed + ", marked " + __instance.MarkedForClose +
                                                   ") asked for its matrix without its barrel; gave the block's own");
            }
            return false;
        }
    }
}
