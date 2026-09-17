using System;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Limits a refinery pull so that no refinery holds more than its fair share of the ore in its
    /// conveyor network. Vanilla lets every refinery pull OreAmountPerPullRequest (2000 kg) per tick
    /// until its input is 60% full, so whichever refineries reach the ore first fill up while others
    /// get nothing - and a refinery refines at a fixed speed, so the hoard is refined slowly while
    /// its neighbours idle. The share is all ore of the network (sources plus every refinery input)
    /// divided by the number of refineries.
    /// </summary>
    public static class OreFairShare
    {
        /// <summary>A nearly empty refinery may always pull at least this much.</summary>
        public const double MinPullKg = 100;

        /// <returns>How much this refinery may pull now (0: it already holds its share).</returns>
        public static double Allowed(double requested, double sourceOre, double refineryInputOre, int refineries,
            double heldBySelf)
        {
            if (refineries <= 1) return requested;
            var share = (sourceOre + refineryInputOre) / refineries;
            var allowed = share - heldBySelf;
            if (heldBySelf < MinPullKg) allowed = Math.Max(allowed, MinPullKg);
            return Math.Max(0, Math.Min(requested, allowed));
        }
    }
}
