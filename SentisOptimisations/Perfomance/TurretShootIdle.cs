using System;
using System.Reflection;
using Sandbox.Game.Weapons;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin.Perfomance
{
    /// <summary>
    /// A turret told to stop shooting while it is not shooting is not told so over the network.
    ///
    /// <c>IMyUserControllableGun.Shoot = false</c> (a script, the terminal's switch) ends in
    /// <c>MyUserControllableGun.OnShootPressed(false)</c>, and a turret's <c>EndShoot</c> raises <c>OnEndShoot</c>, a
    /// reliable event broadcast to every client, every time. Fire control scripts on Update1 set it to every turret they
    /// hold every tick while they have no target (prod 05.11.2026, dotTrace: 0.4 s of 64 s on the game thread in that
    /// event alone, on top of the traffic, from FCS-ADAPTIVE-CLUSTER on up to 16 turrets a ship, three blocks a ship).
    ///
    /// Left out only when nothing would change: the Shoot switch already off, and the turret neither shooting for a
    /// player (<c>m_isPlayerShooting</c>) nor by itself (<c>m_isShooting</c>) - all that <c>OnEndShoot</c> sets false is
    /// false. Turning shooting on, and any turret that shoots, as ever; fixed weapons (their EndShoot sends nothing) as ever.
    /// </summary>
    [PatchShim]
    public static class TurretShootIdle
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo ForceShootField = typeof(MyUserControllableGun).GetField("m_forceShoot", Any);
        private static readonly FieldInfo IsShootingField = typeof(MyUserControllableGun).GetField("m_isShooting", Any);
        private static readonly FieldInfo PlayerShootingField = typeof(MyLargeTurretBase).GetField("m_isPlayerShooting", Any);

        /// <summary>Stops of turrets not shooting left out since the start.</summary>
        public static long Skipped;

        private static Func<MyUserControllableGun, bool> _forceShoot, _isShooting;
        private static Func<MyLargeTurretBase, bool> _playerShooting;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("TurretShootIdle", ctx, c =>
        {
            if (ForceShootField == null || IsShootingField == null || PlayerShootingField == null)
                throw new MissingFieldException("MyUserControllableGun.m_forceShoot / m_isShooting, MyLargeTurretBase.m_isPlayerShooting");
            _forceShoot = Reader<MyUserControllableGun>(ForceShootField);
            _isShooting = Reader<MyUserControllableGun>(IsShootingField);
            _playerShooting = Getter<MyLargeTurretBase>(PlayerShootingField);
            var pressed = typeof(MyUserControllableGun).GetMethod("OnShootPressed", Any)
                          ?? throw new MissingMethodException("MyUserControllableGun.OnShootPressed");
            c.GetPattern(pressed).Prefixes.Add(typeof(TurretShootIdle).GetMethod(nameof(PressedPrefix), BindingFlags.Static | BindingFlags.NonPublic));
        });

        /// <summary>A bool sync field's value (Sync&lt;bool, ...&gt;.Value).</summary>
        private static Func<T, bool> Reader<T>(FieldInfo field)
        {
            var value = field.FieldType.GetProperty("Value") ?? throw new MissingMemberException(field.FieldType.Name + ".Value");
            var p = System.Linq.Expressions.Expression.Parameter(typeof(T));
            var body = System.Linq.Expressions.Expression.Property(System.Linq.Expressions.Expression.Field(p, field), value);
            return System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(body, p).Compile();
        }

        private static Func<T, bool> Getter<T>(FieldInfo field)
        {
            var p = System.Linq.Expressions.Expression.Parameter(typeof(T));
            return System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(System.Linq.Expressions.Expression.Field(p, field), p).Compile();
        }

        /// <summary>Whether a stop of this gun would change nothing: a turret with the switch off and not shooting.</summary>
        public static bool Idle(MyUserControllableGun gun)
        {
            return gun is MyLargeTurretBase turret && !_forceShoot(gun) && !_isShooting(gun) && !_playerShooting(turret);
        }

        private static bool PressedPrefix(MyUserControllableGun __instance, bool isShooting)
        {
            if (isShooting || !(SentisOptimisationsPlugin.Config?.TurretShootIdle ?? true)) return true;
            try
            {
                if (!Idle(__instance)) return true;
            }
            catch (Exception)
            {
                return true;
            }
            System.Threading.Interlocked.Increment(ref Skipped);
            return false;
        }
    }
}
