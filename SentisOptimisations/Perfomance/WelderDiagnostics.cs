using System.Threading;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// What the welders actually did, counted. A ship that hovers over a blueprint and builds
    /// nothing looks the same from the outside whatever the reason - no permit for this frame, no
    /// component in hand, nothing found to build - and these counters are what tells those apart.
    ///
    /// Plain counters on the game thread; the tests read them through <see cref="Snapshot"/>.
    /// </summary>
    public static class WelderDiagnostics
    {
        public static long Activations;      // welder activations that reached the plugin
        public static long Welded;           // activations that welded at least one existing block
        public static long Scans;            // projection scans run
        public static long NoPermit;         // scans skipped: another welder had this frame's build
        public static long Candidates;       // buildable projected blocks found
        public static long Builds;           // projected blocks materialized
        public static long NoComponents;     // candidates skipped: the tool held none of the component
        public static long OverLimits;       // candidates skipped: world block limits
        public static long BudgetSpent;      // candidates skipped: the frame's builds were used up
        public static long PullsQueued;      // component pulls left to the conveyor system
        public static long PullsImmediate;   // component pulls fetched synchronously

        public static void Count(ref long counter) => Interlocked.Increment(ref counter);

        public static string Snapshot() =>
            $"activations={Activations} welded={Welded} scans={Scans} noPermit={NoPermit} " +
            $"candidates={Candidates} builds={Builds} noComponents={NoComponents} overLimits={OverLimits} " +
            $"budgetSpent={BudgetSpent} pulls={PullsQueued}q/{PullsImmediate}i";

        public static void Reset()
        {
            Activations = Welded = Scans = NoPermit = Candidates = Builds = 0;
            NoComponents = OverLimits = BudgetSpent = PullsQueued = PullsImmediate = 0;
        }
    }
}
