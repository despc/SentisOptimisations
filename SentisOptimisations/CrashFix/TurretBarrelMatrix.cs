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
    /// <c>MyLargeTurretBase.GetWorldMatrix()</c> is <c>Barrel.Entity.WorldMatrix</c>, and a turret can be without its
    /// barrel: a closed one (<c>OnClose</c> closes it and sets it to null) and one damaged below its built state, which
    /// stays in the world (<c>OnModelChange</c> of the gatling, missile and interior turrets drops the barrel when the
    /// block is not <c>IsBuilt</c>). A character's target lock keeps the turret it locks from
    /// (<c>MyTargetLockingComponent.m_controlledBlock</c>) until its controlled entity changes, and the lock's check of
    /// the target's visibility on the server (<c>UpdateBeforeSimulation</c> → <c>CanTarget</c>) asked such a turret for
    /// its matrix: a NullReferenceException in the session components' update, which no finalizer covers, and the server
    /// went down (08.11.2026 17:18, production; a player left 1.3 s before, which of the two it was is not known).
    /// <c>MyTargetFocusComponent.UpdateAimVector</c> asks the same. The block's own matrix is where the barrel stands,
    /// near enough for whoever asks; the lock goes as the game lets go of it. Every such turret is written to the log
    /// once a minute, with its closed, marked and built flags to tell the two apart.
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
            try
            {
                if ((DateTime.UtcNow - _loggedAt).TotalSeconds >= 60)
                {
                    _loggedAt = DateTime.UtcNow;
                    SentisOptimisationsPlugin.Log.Warn("Turret " + __instance.EntityId + " '" + __instance.CustomName + "' on '" + __instance.CubeGrid?.DisplayName +
                                                       "' (closed " + __instance.Closed + ", marked " + __instance.MarkedForClose + ", built " + __instance.IsBuilt +
                                                       ") asked for its matrix without its barrel; gave the block's own");
                }
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "TurretBarrelMatrix");
            }
            return false;
        }
    }
}
