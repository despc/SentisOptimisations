using System;
using System.Reflection;
using System.Threading;
using Sandbox.Game.Weapons;
using Torch.Managers.PatchManager;

namespace SentisOptimisations.Slowdowns
{
    /// <summary>
    /// At the peak load a turret without a target is updated every other frame (Slowdowns: "Idle turrets updated once in 3
    /// frames").
    ///
    /// A turret's per-frame update (<c>MyLargeTurretBase.UpdateAfterSimulation</c>: its AI weapon - turning to its
    /// target or idling round -, the rotation sent to the clients, the shooting) runs every frame whether it has a target
    /// or not: 2 s of a minute's trace of the live server at its peak (11.11.2026), on the clusters' threads. Here a
    /// turret with no target, not controlled by a player, runs it on one frame in <see cref="Period"/>, each turret on its own frame (its
    /// phase from its entity id: the turrets of a base spread over the 3 frames, not all in one): it idles round at a
    /// third of its speed. One with a target, or under control, as ever; finding a target is the search's
    /// (its own slowdown).
    /// </summary>
    [PatchShim]
    public static class IdleTurretSlowdown
    {
        public const int Period = 3;

        public static long Skipped;

        public static void Patch(PatchContext ctx) => PatchGuard.Run("IdleTurretSlowdown", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var update = typeof(MyLargeTurretBase).GetMethod("UpdateAfterSimulation", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)
                         ?? throw new MissingMethodException("MyLargeTurretBase", "UpdateAfterSimulation");
            ctx.GetPattern(update).Prefixes.Add(typeof(IdleTurretSlowdown).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool Prefix(MyLargeTurretBase __instance)
        {
            if (!PeakLoad.Active) return true;
            var config = SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config;
            if (config == null || !config.SlowIdleTurrets) return true;
            try
            {
                var turret = (Sandbox.ModAPI.IMyLargeTurretBase)__instance;
                if (turret.HasTarget || turret.IsUnderControl) return true;
                var session = Sandbox.Game.World.MySession.Static;
                if (session == null || PeakLoad.Turn(__instance.EntityId, (ulong)session.GameplayFrameCounter, Period)) return true;
                Interlocked.Increment(ref Skipped);
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
