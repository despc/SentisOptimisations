using System;
using System.Collections.Concurrent;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// Gas tanks and air vents move their gas in one transfer instead of thirty.
    ///
    /// <c>MyGasTank.ExecuteGasTransfer</c> and <c>MyAirVent.Transfer</c> run on every update of every
    /// block, each pushing a sliver of gas through the conveyor network. The sliver is added up here
    /// and handed over once <see cref="TransfersPerFlush"/> of them have gathered, so the network is
    /// walked once instead of thirty times and the gas that moves is the same.
    ///
    /// Held gas is never dropped: a block that stops transferring mid-batch - the tank filled up, the
    /// vent was switched off, the world is unloading - is flushed by the next tick that touches it or
    /// by <see cref="Flush"/>, and what it owes is applied in full. The previous version kept its
    /// slivers in a list until thirty more arrived, so a tank that went quiet at twenty-nine simply
    /// lost them.
    /// </summary>
    [PatchShim]
    public static class GasTankOptimisations
    {
        /// <summary>Transfers gathered before the batch is handed to the game.</summary>
        public const int TransfersPerFlush = 30;

        /// <summary>A batch is also handed over when it has been waiting this long.</summary>
        private static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(2);

        /// <summary>One block's unapplied transfers.</summary>
        private sealed class Batch
        {
            public double Amount;
            public int Count;
            public DateTime Since;

            /// <summary>
            /// Adds one transfer and says whether the batch is due. The amount is returned through
            /// <paramref name="total"/>: either the transfer alone (still gathering) or everything
            /// gathered so far including it (due).
            /// </summary>
            public bool Add(double amount, DateTime now, out double total)
            {
                if (Count == 0) Since = now;
                Amount += amount;
                Count++;
                total = Amount;
                if (Count < TransfersPerFlush && now - Since < MaxHold) return false;
                Reset();
                return true;
            }

            /// <summary>What the block owes, if anything.</summary>
            public bool Take(out double total)
            {
                total = Amount;
                var owed = Count > 0;
                Reset();
                return owed;
            }

            private void Reset()
            {
                Amount = 0;
                Count = 0;
            }
        }

        private static readonly ConcurrentDictionary<long, Batch> Batches = new ConcurrentDictionary<long, Batch>();

        /// <summary>Set while a flush calls the patched method, so its own call is not gathered again.</summary>
        [ThreadStatic] private static bool _flushing;

        private static readonly Action<MyGasTank, double> ExecuteTankTransfer =
            Accessors.Method<MyGasTank, Action<MyGasTank, double>>("ExecuteGasTransfer");
        private static readonly Action<MyAirVent, float> ExecuteVentTransfer =
            Accessors.Method<MyAirVent, Action<MyAirVent, float>>("Transfer");

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("GasTankOptimisations", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            if (ExecuteTankTransfer == null || ExecuteVentTransfer == null)
                throw new InvalidOperationException("GasTankOptimisations: the gas transfer methods could not be bound");

            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            var self = typeof(GasTankOptimisations);

            ctx.GetPattern(typeof(MyGasTank).GetMethod("ExecuteGasTransfer", instance))
                .Prefixes.Add(self.GetMethod(nameof(TankTransferPrefix), statics));
            ctx.GetPattern(typeof(MyAirVent).GetMethod("Transfer", instance))
                .Prefixes.Add(self.GetMethod(nameof(VentTransferPrefix), statics));
        }

        private static bool TankTransferPrefix(MyGasTank __instance, ref double totalTransfer)
        {
            if (!Gather(__instance, totalTransfer, out var total)) return false;
            totalTransfer = total;
            return true;
        }

        private static bool VentTransferPrefix(MyAirVent __instance, ref float transferAmount)
        {
            if (!Gather(__instance, transferAmount, out var total)) return false;
            transferAmount = (float)total;
            return true;
        }

        /// <summary>
        /// True when the block's transfer should go through now, with <paramref name="total"/> being
        /// what it should move; false while the transfer is only being gathered.
        /// </summary>
        private static bool Gather(MyEntity block, double amount, out double total)
        {
            total = amount;
            try
            {
                if (_flushing || !SentisOptimisationsPlugin.Config.GasTankOptimisation || amount == 0.0) return true;
                var batch = Batches.GetOrAdd(block.EntityId, _ => new Batch());
                lock (batch)
                {
                    return batch.Add(amount, DateTime.UtcNow, out total);
                }
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "Gas transfer batching failed; falling back to vanilla");
                return true;
            }
        }

        /// <summary>
        /// Hands over what a block still owes. Called when the block goes away and when the world
        /// unloads, so gathered gas is never left behind.
        /// </summary>
        public static void Flush(MyEntity block)
        {
            if (block == null || !Batches.TryRemove(block.EntityId, out var batch)) return;
            double owed;
            lock (batch)
            {
                if (!batch.Take(out owed)) return;
            }

            if (owed == 0.0) return;
            try
            {
                _flushing = true;
                if (block is MyGasTank tank) ExecuteTankTransfer(tank, owed);
                else if (block is MyAirVent vent) ExecuteVentTransfer(vent, (float)owed);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "Flushing a gas transfer failed");
            }
            finally
            {
                _flushing = false;
            }
        }

        /// <summary>Drops per-entity state when an entity is destroyed (called from EntitiesObserver).</summary>
        public static void CleanupEntity(MyEntity entity)
        {
            if (entity is MyGasTank || entity is MyAirVent) Flush(entity);
        }

        /// <summary>Flushes everything that is still held; the world is going away.</summary>
        public static void ClearAll()
        {
            foreach (var id in Batches.Keys)
            {
                if (MyEntities.TryGetEntityById(id, out var entity)) Flush(entity);
                else Batches.TryRemove(id, out _);
            }
        }
    }
}
