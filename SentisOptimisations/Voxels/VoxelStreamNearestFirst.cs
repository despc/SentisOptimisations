using System;
using System.Linq.Expressions;
using System.Reflection;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using VRage.Network;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The voxels a client is streamed go nearest to its player first.
    ///
    /// A client is streamed one replicable at a time: of the state groups due in its queue the server takes the first
    /// streaming one (<c>MyReplicationServer.FilterStateSync</c>), and a group's place in the queue is the current
    /// frame plus a random number of frames up to twice its layer's send interval
    /// (<c>ScheduleStateGroupSync</c>). The layer follows the distance only roughly - 20 m to 15 km in six steps -
    /// and within a layer the order is chance: a player who arrives among asteroids waited for far ones while the one
    /// at hand had not come yet.
    ///
    /// Here a voxel map's streaming group is put in the queue by the distance from the client's player to the voxel
    /// map itself (zero inside its box, so the planet a player stands on comes first): one frame later for every
    /// <see cref="MetresPerFrame"/>. Nothing else changes: the other state groups keep the game's order, and a
    /// stream is still one at a time.
    /// </summary>
    [PatchShim]
    public static class VoxelStreamNearestFirst
    {
        /// <summary>Distance that puts a voxel map's stream one frame later in the queue.</summary>
        public const double MetresPerFrame = 250;

        private static Type _voxelReplicable;
        private static Func<object, MyVoxelBase> _voxel;
        private static Func<object, MyClientStateBase> _state;
        // the client's DirtyQueue (a FastPriorityQueue<MyStateDataEntry>), asked through delegates made for its type
        private static Func<object, MyStateDataEntry, bool> _queued;
        private static Action<object, MyStateDataEntry, long> _reschedule;

        // Read by SentisTests.
        public static long Placed;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("VoxelStreamNearestFirst", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            _voxelReplicable = typeof(MyVoxelBase).Assembly.GetType("Sandbox.Game.Replication.MyVoxelReplicable")
                               ?? throw new TypeLoadException("Sandbox.Game.Replication.MyVoxelReplicable");
            var instance = _voxelReplicable.GetProperty("Instance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)
                           ?? throw new MissingMemberException("MyVoxelReplicable.Instance");
            var clientType = typeof(MyReplicationServer).Assembly.GetType("VRage.Network.MyClient", true);
            var state = clientType.GetField("State", BindingFlags.Instance | BindingFlags.Public) ?? throw new MissingFieldException("MyClient.State");
            var queue = clientType.GetField("DirtyQueue", BindingFlags.Instance | BindingFlags.Public) ?? throw new MissingFieldException("MyClient.DirtyQueue");
            var schedule = typeof(MyReplicationServer).GetMethod("ScheduleStateGroupSync", BindingFlags.Instance | BindingFlags.NonPublic)
                           ?? throw new MissingMethodException("MyReplicationServer.ScheduleStateGroupSync");

            var o = Expression.Parameter(typeof(object));
            _voxel = Expression.Lambda<Func<object, MyVoxelBase>>(
                Expression.Convert(Expression.Property(Expression.Convert(o, _voxelReplicable), instance), typeof(MyVoxelBase)), o).Compile();
            _state = Expression.Lambda<Func<object, MyClientStateBase>>(Expression.Field(Expression.Convert(o, clientType), state), o).Compile();
            var entry = Expression.Parameter(typeof(MyStateDataEntry));
            var at = Expression.Parameter(typeof(long));
            var dirty = Expression.Field(Expression.Convert(o, clientType), queue);
            var contains = queue.FieldType.GetMethod("Contains") ?? throw new MissingMethodException("DirtyQueue.Contains");
            var update = queue.FieldType.GetMethod("UpdatePriority") ?? throw new MissingMethodException("DirtyQueue.UpdatePriority");
            _queued = Expression.Lambda<Func<object, MyStateDataEntry, bool>>(Expression.Call(dirty, contains, entry), o, entry).Compile();
            _reschedule = Expression.Lambda<Action<object, MyStateDataEntry, long>>(
                Expression.Call(dirty, update, entry, Expression.Convert(at, update.GetParameters()[1].ParameterType)), o, entry, at).Compile();

            // a Torch suffix: IdleInventorySync puts a Torch prefix on this method, and Torch's rewrite of it drops a
            // Harmony patch made before (placed 0 on the stand)
            ctx.GetPattern(schedule).Suffixes.Add(typeof(VoxelStreamNearestFirst).GetMethod(nameof(SchedulePostfix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        // MyReplicationServer.ScheduleStateGroupSync(MyClient client, MyStateDataEntry groupEntry, long currentTime, bool allowReplicableRemoval)
        private static void SchedulePostfix(object client, MyStateDataEntry groupEntry, long currentTime)
        {
            try
            {
                if (groupEntry?.Group == null || !groupEntry.Group.IsStreaming) return;
                var owner = groupEntry.Owner;
                if (owner == null || !_voxelReplicable.IsInstanceOfType(owner)) return;
                var at = _state(client)?.Position;
                if (!at.HasValue) return;
                var voxel = _voxel(owner);
                if (voxel == null || voxel.Closed) return;
                if (!_queued(client, groupEntry)) return;
                var due = Due(currentTime, voxel.PositionComp.WorldAABB.Distance(at.Value));
                _reschedule(client, groupEntry, due);
                System.Threading.Interlocked.Increment(ref Placed);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "VoxelStreamNearestFirst");
            }
        }

        /// <summary>The frame a voxel map's stream is due at: the next one, and one more for every <see cref="MetresPerFrame"/> away.</summary>
        public static long Due(long currentTime, double distance) => currentTime + 1 + (long)(Math.Max(0, distance) / MetresPerFrame);
    }
}
