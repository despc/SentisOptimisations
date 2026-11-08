using System;
using System.Reflection;
using System.Threading;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// An air vent's per-frame update runs in full once in <see cref="IdlePeriod"/> frames when it moves no gas, once in
    /// <see cref="ActivePeriod"/> when it does; between them a vent with gas only moves its gas.
    ///
    /// A vent in an airtight room with less air than its lower limit stays on per-frame updates while it has nothing to
    /// give: <c>ExecuteGasTransfer</c> takes the vent off them, and <c>ResourceSink.Update</c> in the same call
    /// (<c>Sink_ComputeRequiredGas</c>) puts it back - and so does the status Pressurizing. Each frame such a vent
    /// looked up its room three times (the gas it asks for, its status, its lights) and moved nothing: 1.1 s a minute
    /// in the clusters, the dearest kind of block there (prod 08.11.2026, the transfer itself not in the trace).
    ///
    /// The frame a vent runs in full is its own (from its entity id), so the vents of the world run spread over the
    /// frames instead of all in one. The gas is moved every frame as the original moves it (<c>Transfer</c> of
    /// <c>GasInputPerUpdate - GasOutputPerUpdate</c>, which <see cref="GasTankOptimisations"/> gathers), so none is
    /// lost and a vent whose gas starts coming in moves it from that frame on. What waits for the full run is the gas
    /// the vent asks for (its sink's update), its status, its lights and its detailed info; its own 100th-frame update
    /// refreshes them too. A vent with its damage effect on runs every frame.
    /// </summary>
    [PatchShim]
    public static class AirVentIdle
    {
        /// <summary>The frames between two full runs of a vent that moves no gas.</summary>
        public const int IdlePeriod = 100;

        /// <summary>The frames between two full runs of a vent that moves gas.</summary>
        public const int ActivePeriod = 10;

        /// <summary>Updates of vents left out (for the tests): of idle vents, and of vents that only moved their gas.</summary>
        public static long Skipped, SkippedActive;

        private static readonly Func<MyCubeBlock, bool> HasDamageEffect =
            Accessors.Method<MyCubeBlock, Func<MyCubeBlock, bool>>("get_HasDamageEffect");

        private static readonly Action<MyAirVent, float> Transfer =
            Accessors.Method<MyAirVent, Action<MyAirVent, float>>("Transfer");

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("AirVentIdle", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var target = typeof(MyAirVent).GetMethod(nameof(MyAirVent.UpdateAfterSimulation), BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (target == null || target.DeclaringType != typeof(MyAirVent))
                throw new InvalidOperationException("AirVentIdle: MyAirVent.UpdateAfterSimulation not found");
            ctx.GetPattern(target).Prefixes.Add(typeof(AirVentIdle).GetMethod(nameof(UpdatePrefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>The frame of the idle period this entity runs on: its own, spread evenly.</summary>
        public static int Phase(long entityId) => (int)(((ulong)entityId ^ ((ulong)entityId >> 32)) % IdlePeriod);

        /// <summary>Whether a vent with this id runs in full on this frame, once in <paramref name="period"/> frames.</summary>
        public static bool Due(long entityId, int frame, int period = IdlePeriod) => (uint)frame % (uint)period == Phase(entityId) % period;

        private static bool UpdatePrefix(MyAirVent __instance)
        {
            try
            {
                if (!SentisOptimisationsPlugin.Config.AirVentIdleUpdates) return true;
                var session = MySession.Static;
                if (session == null || HasDamageEffect(__instance)) return true;
                var gas = __instance.GasInputPerUpdate - __instance.GasOutputPerUpdate;
                var frame = session.GameplayFrameCounter;
                if (gas == 0f)
                {
                    if (Due(__instance.EntityId, frame)) return true;
                    Interlocked.Increment(ref Skipped);
                    return false;
                }
                if (Due(__instance.EntityId, frame, ActivePeriod)) return true;
                Transfer(__instance, gas);
                Interlocked.Increment(ref SkippedActive);
                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}
