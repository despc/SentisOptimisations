using System;
using System.Reflection;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Multiplayer;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// A jump at the drives' full reach is not aborted because the ship's reach shrank a little while it counted down.
    ///
    /// At a jump's start the server cuts the jump to the drives' reach (<c>MyGridJumpDriveSystem.Jump</c>,
    /// <c>GetMaxJumpDistance</c>); at the countdown's end it asks for the reach again and aborts the jump, without a word,
    /// when the jump is longer (<c>UpdateJumpDriveSystem</c>). The reach of a ship heavier than its drives' MaxJumpMass is
    /// less as its mass grows, and a working ship's mass moves all the time - gas in its tanks, ice in its generators: a
    /// jump at the full reach failed nearly always. The prod's "БурПрыг" (2656 t, stand copy, 30.10.2026, SentisTests'
    /// jump_probe): asked 941 180.0 m, the mass up 16 kg in the 10 s, the reach 941 165 m at the end - aborted, the ship
    /// left where it was.
    ///
    /// Here a jump longer than the reach at the end by no more than <see cref="Slack"/> is cut to the reach then, as the
    /// game cuts it at the start; a reach lost for real (drives off, emptied, taken) still aborts it.
    /// </summary>
    [PatchShim]
    public static class JumpReachDrift
    {
        /// <summary>How much longer than the reach at the countdown's end a jump may be and still go, cut to the reach.</summary>
        private const double Slack = 0.05;

        private const BindingFlags Own = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo TimeLeft = typeof(MyGridJumpDriveSystem).GetField("m_jumpTimeLeft", Own);
        private static readonly FieldInfo Jumped = typeof(MyGridJumpDriveSystem).GetField("m_jumped", Own);
        private static readonly FieldInfo Direction = typeof(MyGridJumpDriveSystem).GetField("m_jumpDirection", Own);
        private static readonly FieldInfo Destination = typeof(MyGridJumpDriveSystem).GetField("m_selectedDestination", Own);
        private static readonly FieldInfo User = typeof(MyGridJumpDriveSystem).GetField("m_userId", Own);

        /// <summary>Jumps cut to the reach at the countdown's end, since the start (read by SentisTests).</summary>
        public static long Cut;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("JumpReachDrift", ctx, c =>
        {
            if (TimeLeft == null || Jumped == null || Direction == null || Destination == null || User == null)
                throw new MissingFieldException("MyGridJumpDriveSystem: a field of the jump is not in this game");
            var update = typeof(MyGridJumpDriveSystem).GetMethod("UpdateJumpDriveSystem", Own, null, Type.EmptyTypes, null)
                         ?? throw new MissingMethodException("MyGridJumpDriveSystem.UpdateJumpDriveSystem");
            c.GetPattern(update).Prefixes.Add(typeof(JumpReachDrift).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        });

        // the countdown's end is this update's when the time left is out and the jump not made (the game's own branch)
        private static void Prefix(MyGridJumpDriveSystem __instance)
        {
            if (!__instance.IsJumping || !Sync.IsServer) return;
            if ((float)TimeLeft.GetValue(__instance) > 0f || (bool)Jumped.GetValue(__instance)) return;
            var direction = (Vector3D)Direction.GetValue(__instance);
            var length = direction.Length();
            var reach = __instance.GetMaxJumpDistance((long)User.GetValue(__instance));
            if (reach <= 0 || length <= reach || length > reach * (1 + Slack)) return;
            var cut = direction * (reach / length);
            Direction.SetValue(__instance, cut);
            Destination.SetValue(__instance, (Vector3D)Destination.GetValue(__instance) - (direction - cut));
            System.Threading.Interlocked.Increment(ref Cut);
            if (global::SentisOptimisations.DiagLog.On)
                SentisOptimisationsPlugin.Log.Info($"JumpReachDrift: a jump of {length:F1} m cut to the reach {reach:F1} m at the countdown's end");
        }
    }
}
