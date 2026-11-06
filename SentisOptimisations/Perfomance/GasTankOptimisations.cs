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
    /// lost them. A tank's update with nothing to transfer hands over what it still holds.
    ///
    /// A held transfer still counts for the tank's own bookkeeping (<c>m_prevSumTotalTransfer</c>,
    /// added up by <c>ExecuteGasTransfer</c>). Every 100 frames a tank that saw no transfer since the
    /// last time stops its per-frame updates, and nothing restarts them while its input stays the same.
    /// The withheld calls left the sum at zero: a tank whose 100-frame check came before its first
    /// batch of thirty stopped for good - its generator went on making gas and burning ice, the gas
    /// went nowhere (found by reg_generator_pulls_ice: 1000 kg of ice into a tank left empty).
    ///
    /// A projection's blocks are left to the game, and a batch is handed over only to the block it was gathered for.
    /// A block a projector builds takes the entity id of its ghost in the projection, and the ghost tank goes on being
    /// updated with nothing to transfer - which hands over what is held: the ghost took the built tank's gas every
    /// frame and the tank stayed empty while the ship's tanks gave to it (prod 06.11.2026, torpedoes welded on their
    /// racks took 750 L/s each and filled not at all; SentisTests torpedo_refill). The batches stay by entity id: a block
    /// a split or a merge makes again (a new object, the id kept) takes what its old self still held.
    ///
    /// A projection's tanks are not updated at all. The projection turns its blocks' updates off, but a tank turns its
    /// per-frame update back on whenever its gas input or output changes - and the projection's own resource system
    /// changes them - while the 100-frame check that would turn it off again stays off: every ghost tank ran an empty
    /// transfer every frame for as long as it was projected (the stand on the prod save, 06.11.2026: 230 of them, the
    /// only ghosts of 14016 updated; SentisTests projection_twins).
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
            // the block it is gathered for (weakly: a batch left behind keeps no block alive)
            public readonly WeakReference<MyEntity> Owner = new WeakReference<MyEntity>(null);

            /// <summary>
            /// Whether <paramref name="block"/> may take this batch: its own, or it was gathered for one gone - the same
            /// block made again by a split or a merge. Another live block of the same id (a projection's ghost) may not.
            /// </summary>
            public bool Belongs(MyEntity block)
            {
                if (!Owner.TryGetTarget(out var owner) || ReferenceEquals(owner, block) || owner.Closed || owner.MarkedForClose)
                {
                    Owner.SetTarget(block);
                    return true;
                }
                return false;
            }

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
        private static readonly Func<MyGasTank, double> PrevSumTotalTransfer = Accessors.Field<MyGasTank, double>("m_prevSumTotalTransfer");
        private static readonly Action<MyGasTank, double> SetPrevSumTotalTransfer = Accessors.SetField<MyGasTank, double>("m_prevSumTotalTransfer");
        private static readonly Action<MyAirVent, float> ExecuteVentTransfer =
            Accessors.Method<MyAirVent, Action<MyAirVent, float>>("Transfer");

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("GasTankOptimisations", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            if (ExecuteTankTransfer == null || ExecuteVentTransfer == null)
                throw new InvalidOperationException("GasTankOptimisations: the gas transfer methods could not be bound");
            if (PrevSumTotalTransfer == null || SetPrevSumTotalTransfer == null)
                throw new InvalidOperationException("GasTankOptimisations: MyGasTank.m_prevSumTotalTransfer could not be bound");

            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            var self = typeof(GasTankOptimisations);

            ctx.GetPattern(typeof(MyGasTank).GetMethod("ExecuteGasTransfer", instance))
                .Prefixes.Add(self.GetMethod(nameof(TankTransferPrefix), statics));
            ctx.GetPattern(typeof(MyAirVent).GetMethod("Transfer", instance))
                .Prefixes.Add(self.GetMethod(nameof(VentTransferPrefix), statics));
            ctx.GetPattern(typeof(MyGasTank).GetMethod(nameof(MyGasTank.UpdateAfterSimulation), BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null))
                .Prefixes.Add(self.GetMethod(nameof(TankUpdatePrefix), statics));
        }

        /// <summary>A ghost tank of a projection: its per-frame update off, nothing run.</summary>
        private static bool TankUpdatePrefix(MyGasTank __instance)
        {
            if (!InProjection(__instance)) return true;
            __instance.NeedsUpdate &= ~VRage.ModAPI.MyEntityUpdateEnum.EACH_FRAME_AFTER;
            return false;
        }

        private static bool TankTransferPrefix(MyGasTank __instance, ref double totalTransfer)
        {
            if (!Gather(__instance, totalTransfer, out var total))
            {
                // what the skipped original would have counted: the tank keeps its per-frame updates
                SetPrevSumTotalTransfer(__instance, PrevSumTotalTransfer(__instance) + totalTransfer);
                return false;
            }
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
                if (_flushing || !SentisOptimisationsPlugin.Config.GasTankOptimisation) return true;
                // a projection's ghost: the game's own way, nothing held for it
                if (InProjection(block)) return true;
                if (amount == 0.0)
                {
                    // the flow has stopped: what is still held goes through with this update
                    if (Batches.TryGetValue(block.EntityId, out var held))
                        lock (held)
                            if (held.Belongs(block)) held.Take(out total);
                    return true;
                }
                var batch = Batches.GetOrAdd(block.EntityId, _ => new Batch());
                lock (batch)
                {
                    // another live block's batch under this id: this one goes the game's way
                    if (!batch.Belongs(block)) return true;
                    return batch.Add(amount, DateTime.UtcNow, out total);
                }
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "Gas transfer batching failed; falling back to vanilla");
                return true;
            }
        }

        /// <summary>Whether the block is a ghost of a projection (a block not yet in the world reads as not).</summary>
        private static bool InProjection(MyEntity block)
        {
            try
            {
                return (block as MyCubeBlock)?.SlimBlock?.CubeGrid?.IsPreview == true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Hands over what a block still owes. Called when the block goes away and when the world
        /// unloads, so gathered gas is never left behind.
        /// </summary>
        public static void Flush(MyEntity block)
        {
            if (block == null || !Batches.TryGetValue(block.EntityId, out var batch)) return;
            double owed;
            lock (batch)
            {
                if (!batch.Belongs(block)) return;
                Batches.TryRemove(block.EntityId, out _);
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
