using System;
using System.Reflection;
using NLog;
using SpaceEngineers.Game.EntityComponents.GameLogic;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin.Freezer
{
    /// <summary>
    /// A plant stops growing while its grid is frozen.
    ///
    /// The freezer takes a grid off the update lists, but a farm plot's growth does not live on the
    /// block - it lives in <see cref="MyFarmPlotLogic"/>, an entity component that registers itself
    /// and re-arms that registration whenever the block's state changes. So a frozen plot kept
    /// growing, kept drinking its water and kept losing health to frost, on a grid that was
    /// otherwise standing perfectly still.
    ///
    /// Clearing the component's update flag does not hold - vanilla puts it back. Skipping the
    /// update while the grid is frozen does, and it leaves the catch-up
    /// (<see cref="FrozenProduction"/>) as the only thing that moves the plant forward: on the thaw
    /// the missed updates are run one after another, so water, temperature and growth stages come
    /// out exactly where vanilla would have put them.
    /// </summary>
    [PatchShim]
    public static class FarmPlotFreeze
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("FarmPlotFreeze", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var update = typeof(MyFarmPlotLogic).GetMethod(nameof(MyFarmPlotLogic.UpdateAfterSimulation100),
                BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (update == null) throw new MissingMethodException("MyFarmPlotLogic.UpdateAfterSimulation100");

            ctx.GetPattern(update).Prefixes.Add(typeof(FarmPlotFreeze)
                .GetMethod(nameof(UpdatePrefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>False skips vanilla's growth: the grid this plot sits on is frozen.</summary>
        private static bool UpdatePrefix(MyFarmPlotLogic __instance)
        {
            try
            {
                var entity = __instance?.Entity;
                return entity == null || !FrozenProduction.IsFrozen(entity.EntityId);
            }
            catch (Exception e)
            {
                Log.Error(e, "Deciding whether a plant is frozen failed");
                return true;
            }
        }
    }
}
