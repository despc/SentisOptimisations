using System;
using System.Collections.Generic;
using System.Reflection;
using Torch.Managers.PatchManager;
using VRage.Network;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Allocation-free default equality for the network endpoint structs.
    ///
    /// VRage.Network.Endpoint and EndpointId do not implement IEquatable, so every dictionary keyed
    /// by them (per-client data of every state group, the replication server's client table, ...)
    /// uses ObjectEqualityComparer, which boxes the key on each lookup. With many clients these
    /// lookups run hundreds of thousands of times per second in MyReplicationServer.SendUpdate and
    /// client ack handling, and the boxes are a large part of the garbage it produces.
    ///
    /// The fix replaces EqualityComparer&lt;T&gt;.Default for both types at plugin load, before the
    /// world (and its dictionaries) is created. The comparers give exactly the vanilla results: the
    /// same fields are compared and the structs' own GetHashCode is used.
    /// </summary>
    [PatchShim]
    public static class EndpointComparers
    {
        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("EndpointComparers", ctx, _ => Install());

        public static void Install()
        {
            SetDefault(EndpointComparer.Instance);
            SetDefault(EndpointIdComparer.Instance);
        }

        private static void SetDefault<T>(EqualityComparer<T> comparer)
        {
            var field = typeof(EqualityComparer<T>).GetField("defaultComparer", BindingFlags.Static | BindingFlags.NonPublic)
                        ?? throw new MissingFieldException("EqualityComparer<T>.defaultComparer");
            field.SetValue(null, comparer);
        }

        public sealed class EndpointComparer : EqualityComparer<Endpoint>
        {
            public static readonly EndpointComparer Instance = new EndpointComparer();
            public override bool Equals(Endpoint x, Endpoint y) => x.Id == y.Id && x.Index == y.Index;
            public override int GetHashCode(Endpoint obj) => obj.GetHashCode();
        }

        public sealed class EndpointIdComparer : EqualityComparer<EndpointId>
        {
            public static readonly EndpointIdComparer Instance = new EndpointIdComparer();
            public override bool Equals(EndpointId x, EndpointId y) => x == y;
            public override int GetHashCode(EndpointId obj) => obj.GetHashCode();
        }
    }
}
