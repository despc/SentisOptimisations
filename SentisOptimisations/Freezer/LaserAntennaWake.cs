using System;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin.Freezer
{
    /// <summary>
    /// A laser antenna reaching for another one keeps that other grid awake.
    ///
    /// Connecting two laser antennas is a conversation: each end turns its dish towards the other,
    /// and the handshake only goes through while both are enabled, powered and pointing at each
    /// other. A frozen grid runs no updates at all, so its dish never turns - a player could not
    /// connect to their own ship parked a few kilometres away, because the freezer had it.
    ///
    /// So the end that is awake asks for the other end to be kept awake: when the connection is
    /// requested, and again on every pass while it is turning towards it, contacting it or connected
    /// to it. The request lasts <see cref="KeepAwakeSeconds"/> and renews itself while the link is
    /// being held.
    ///
    /// Only an end that a player is standing by renews it, though. Two linked antennas point at each
    /// other, so each end would otherwise renew the other one for ever: nothing ever freezes again,
    /// and a chain of links keeps the whole map awake. Asking who is near, rather than whether the
    /// grid happens to be running, breaks that circle - the far end lives off the player at the near
    /// end, and goes back to sleep once they leave.
    /// </summary>
    [PatchShim]
    public static class LaserAntennaWake
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>How long one request holds the other end awake.</summary>
        private const double KeepAwakeSeconds = 20;

        private static Func<MyLaserAntenna, long?> _targetId;

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("LaserAntennaWake", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            var self = typeof(LaserAntennaWake);

            _targetId = Accessors.Field<MyLaserAntenna, long?>("m_targetId");

            // The terminal's connect button, and the state machine that carries the link.
            var connectTo = typeof(MyLaserAntenna).GetMethod("ConnectTo", any, null, new[] { typeof(long) }, null);
            if (connectTo == null) throw new MissingMethodException("MyLaserAntenna.ConnectTo");
            ctx.GetPattern(connectTo).Prefixes.Add(self.GetMethod(nameof(ConnectToPrefix), statics));

            var update = typeof(MyLaserAntenna).GetMethod(nameof(MyLaserAntenna.UpdateAfterSimulation100),
                BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (update == null) throw new MissingMethodException("MyLaserAntenna.UpdateAfterSimulation100");
            ctx.GetPattern(update).Prefixes.Add(self.GetMethod(nameof(UpdatePrefix), statics));
        }

        /// <summary>
        /// Somebody asked this antenna to connect: the other end has to be able to answer. The
        /// parameter has to be named exactly as the game names it - Torch binds prefix arguments by
        /// name, and a wrong one fails the whole patch commit, not just this class.
        /// </summary>
        private static void ConnectToPrefix(long DestId)
        {
            Wake(DestId);
        }

        /// <summary>
        /// And on every pass of an antenna that is reaching for, or holding, a link. The prefix runs
        /// before the state machine, so the other end is awake by the time this one looks at it.
        /// </summary>
        private static void UpdatePrefix(MyLaserAntenna __instance)
        {
            try
            {
                if (__instance == null || !__instance.Enabled) return;
                if (__instance.State == MyLaserAntenna.StateEnum.idle) return;
                if (!HeldByPlayer(__instance.CubeGrid)) return;
                var target = _targetId(__instance);
                if (target.HasValue) Wake(target.Value);
            }
            catch (Exception e)
            {
                Log.Error(e, "Keeping a laser antenna's target awake failed");
            }
        }

        /// <summary>
        /// Whether this grid is awake because a player is near it, by the same distance the freezer
        /// uses. The freezer works on whole mechanical groups and measures from one grid of the
        /// group; measuring from this one is the same answer for anything a laser antenna sits on.
        /// </summary>
        private static bool HeldByPlayer(MyCubeGrid grid)
        {
            if (grid == null || grid.MarkedForClose) return false;
            var config = SentisOptimisationsPlugin.Config;
            var distance = grid.IsStatic ? config.FreezeDistanceStatic : config.FreezeDistanceDynamic;
            return PlayerAnchors.AnyInRadius(grid.PositionComp.GetPosition(), distance);
        }

        private static void Wake(long entityId)
        {
            try
            {
                if (entityId == 0) return;
                if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity)) return;
                WakeRequests.Keep(entity, KeepAwakeSeconds);
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not wake a laser antenna's target");
            }
        }
    }
}
