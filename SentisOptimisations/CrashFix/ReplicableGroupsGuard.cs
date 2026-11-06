using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using VRage.Network;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// A replicable destroyed with no state groups of its own does not take the server down.
    ///
    /// <c>MyReplicationServer.Destroy</c> goes on for a replicable that has its network id and reads its state groups
    /// with no check (<c>RemoveStateGroups</c>: <c>m_replicableGroups[replicable]</c>). <c>Replicate</c> gives the id
    /// first and the groups after, so one whose groups are gone - its making broken off half way, or its entry lost -
    /// threw KeyNotFoundException out of the frame, a fatal exception (prod 06.11.2026 22:00: an inventory's replicable
    /// destroyed as its entity was deleted, nothing about it in the log before). Here such a replicable is given an empty
    /// list of groups: the game takes it off the clients and drops its id as for any other, there are no groups to
    /// destroy. Each one is written to the log (the first <see cref="TellFirst"/>, then every hundredth): what it was,
    /// whose.
    /// </summary>
    [PatchShim]
    public static class ReplicableGroupsGuard
    {
        private const int TellFirst = 20;
        private static int _seen;

        private static readonly Func<MyReplicationServer, Dictionary<IMyReplicable, List<IMyStateGroup>>> Groups =
            Accessors.Field<MyReplicationServer, Dictionary<IMyReplicable, List<IMyStateGroup>>>("m_replicableGroups");

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("ReplicableGroupsGuard", ctx, c =>
        {
            if (Groups == null) throw new MissingFieldException("MyReplicationServer.m_replicableGroups");
            var method = typeof(MyReplicationServer).GetMethod("RemoveStateGroups", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(IMyReplicable) }, null)
                         ?? throw new MissingMethodException("MyReplicationServer.RemoveStateGroups");
            c.GetPattern(method).Prefixes.Add(typeof(ReplicableGroupsGuard).GetMethod(nameof(RemoveStateGroupsPrefix), BindingFlags.Static | BindingFlags.NonPublic));
        });

        private static void RemoveStateGroupsPrefix(MyReplicationServer __instance, IMyReplicable replicable)
        {
            try
            {
                var groups = Groups(__instance);
                if (replicable == null || groups == null || groups.ContainsKey(replicable)) return;
                groups[replicable] = new List<IMyStateGroup>();
                var seen = Interlocked.Increment(ref _seen);
                if (seen <= TellFirst || seen % 100 == 0)
                    SentisOptimisationsPlugin.Log.Warn($"ReplicableGroupsGuard: {Describe(replicable)} destroyed with no state groups of its own (made half way or its entry lost); " +
                                                       $"taken off the clients without them ({seen} so far), on thread '{Thread.CurrentThread.Name ?? Thread.CurrentThread.ManagedThreadId.ToString()}'");
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "ReplicableGroupsGuard failed");
            }
        }

        private static string Describe(IMyReplicable replicable)
        {
            try
            {
                var text = replicable.GetType().Name + " '" + replicable + "'";
                // an external replicable's object (MyInventoryReplicable is internal): an inventory, its owner
                var instance = replicable.GetType().GetProperty("Instance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(replicable);
                if (instance is VRage.Game.Entity.MyInventoryBase inventory && inventory.Entity is MyEntity owner)
                {
                    var top = owner.GetTopMostParent();
                    text += $": the inventory of {owner.GetType().Name} {owner.EntityId} '{owner.DisplayName}'" +
                            (top != owner ? $" on {top.GetType().Name} {top.EntityId} '{top.DisplayName}'" : "") +
                            $", closed {owner.Closed}, marked for close {owner.MarkedForClose}";
                }
                return text;
            }
            catch (Exception)
            {
                return replicable.GetType().Name;
            }
        }
    }
}
