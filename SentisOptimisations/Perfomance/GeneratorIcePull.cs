using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using SentisOptimisationsPlugin;
using Torch.Managers.PatchManager;
using VRage;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// An O2/H2 generator's pulls of ice that keep finding none wait, as a refinery's of ore do (<see cref="RefineryOrePull"/>).
    ///
    /// Every 100 frames a generator filled below its mark (vanilla's hysteresis, <c>m_actualCheckFillValue</c>: below the
    /// least fill it pulls until the most) asks the conveyor for ice, and on a network with no ice to give the pull walks
    /// every inventory of it to find none - and the generator stays below its mark and asks again 100 frames later, for
    /// good (prod 06.11.2026: 4.4 ms a second of the game thread, the most of the generators' work, under
    /// <c>PullItem</c>'s inventory walk). Here after <see cref="PullBackoff.EmptyPullsBeforeBackoff"/> pulls that brought
    /// nothing the next ones wait 2, 4... pull periods, up to <see cref="MaxBackoffFrames"/>, spread by a per-generator
    /// jitter. The wait ends at once when ore (ice is an ore) is stored into a sending inventory of the generator's logical
    /// grid group or its conveyor graph is rebuilt - the network generation <see cref="RefineryOrePull"/> keeps - so a
    /// generator still takes fresh ice the moment it comes. A pull is otherwise vanilla's: the same mark, the same amount.
    /// </summary>
    [PatchShim]
    public static class GeneratorIcePull
    {
        // a generator pulls in its 100-frame update
        private const long PullPeriodFrames = 100;
        private const long MaxBackoffFrames = 6 * PullPeriodFrames;

        private static readonly ConditionalWeakTable<MyGasGenerator, PullBackoff> States = new ConditionalWeakTable<MyGasGenerator, PullBackoff>();

        private static readonly Func<MyGasGenerator, float> CheckFill = Accessors.Field<MyGasGenerator, float>("m_actualCheckFillValue");
        private static readonly Action<MyGasGenerator, float> SetCheckFill = Accessors.SetField<MyGasGenerator, float>("m_actualCheckFillValue");
        private static readonly Func<MyGasGenerator, MyInventoryConstraint> OreConstraint = Accessors.Field<MyGasGenerator, MyInventoryConstraint>("m_oreConstraint");

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("GeneratorIcePull", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            if (CheckFill == null || SetCheckFill == null || OreConstraint == null)
                throw new InvalidOperationException("GeneratorIcePull: MyGasGenerator's fill mark or ore constraint could not be bound");
            ctx.GetPattern(typeof(MyGasGenerator).GetMethod("GetIceFromConveyorSystem", BindingFlags.Instance | BindingFlags.NonPublic))
                .Prefixes.Add(typeof(GeneratorIcePull).GetMethod(nameof(GetIcePrefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool GetIcePrefix(MyGasGenerator __instance)
        {
            try
            {
                var generator = __instance;
                var inventory = generator.GetInventory();
                var constraint = OreConstraint(generator);
                var system = generator.CubeGrid?.GridSystems?.ConveyorSystem;
                if (inventory == null || constraint == null || system == null) return true;
                if (!(generator.BlockDefinition is MyOxygenGeneratorDefinition definition)) return true;

                var mark = CheckFill(generator);
                if (mark <= inventory.VolumeFillFactor)
                {
                    // filled to its mark: from the most back to the least (vanilla)
                    if (mark == definition.InventoryFillFactorMax) SetCheckFill(generator, definition.InventoryFillFactorMin);
                    return false;
                }

                var frame = MySession.Static.GameplayFrameCounter;
                var generation = RefineryOrePull.Generation(generator.CubeGrid);
                var state = States.GetValue(generator, _ => new PullBackoff());
                if (state.ShouldSkip(frame, MaxBackoffFrames, generation)) return false;

                MyFixedPoint pulled = 0;
                var amount = (MyFixedPoint)(definition.IceConsumptionPerSecond * 60f * definition.FuelPullAmountFromConveyorInMinutes);
                foreach (var id in constraint.ConstrainedIds)
                    pulled += system.PullItem(id, amount, generator, inventory, false, false);
                if (mark == definition.InventoryFillFactorMin) SetCheckFill(generator, definition.InventoryFillFactorMax);

                state.OnResult(frame, pulled > 0, PullPeriodFrames, MaxBackoffFrames, generation, RefineryOrePull.Jitter(generator.EntityId));
                return false;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "GeneratorIcePull failed");
                return true;
            }
        }
    }
}
