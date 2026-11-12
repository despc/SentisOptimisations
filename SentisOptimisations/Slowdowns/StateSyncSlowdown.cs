using System;
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using Torch.Managers.PatchManager;
using VRage.Library;
using VRage.Network;

namespace SentisOptimisations.Slowdowns
{
    /// <summary>
    /// At the peak load the state of what is far from a client is sent to it half as often (Slowdowns: "Far grids synced
    /// less often").
    ///
    /// A state group (a grid's position, its properties) is queued for each client with a delay of its distance layer
    /// (<c>MyReplicationServer.ScheduleStateGroupSync</c>: from every 4 frames within 20 m to every 128 at 15 km, a random
    /// spread over up to twice that); the sending (<c>FilterStateSync</c>, serializing, the queue) was 4.4 s of a minute's
    /// trace of the live server at its peak (11.11.2026). Here an entry newly queued for a client in a layer of
    /// <see cref="FarRadiusM"/> m or more is given twice the game's delay - once, when it is queued, so a busy entry does
    /// not drift further each time it is scheduled again. The client's own character and what it controls, streams and
    /// everything nearer are as ever. Nothing is dropped: the next send carries what changed meanwhile.
    /// </summary>
    [PatchShim]
    public static class StateSyncSlowdown
    {
        /// <summary>Layers of this radius and more count as far (the game's: 20, 80, 320, 1280, 5120 m, the sync distance).</summary>
        public const int FarRadiusM = 1000;

        public static long Held;

        private static Type _clientType;
        private static Func<object, MyClientStateBase> _state;
        private static Func<object, FastPriorityQueue<MyStateDataEntry>> _queue;
        private static Func<object, IDictionary> _layers;
        private static Func<object, int> _layerRadius;
        private static Func<MyStateDataEntry, long> _priority;

        [ThreadStatic] private static bool _wasQueued;

        public static void Patch(PatchContext ctx) => PatchGuard.Run("StateSyncSlowdown", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _clientType = typeof(MyReplicationServer).Assembly.GetType("VRage.Network.MyClient", true);
            _state = Field<MyClientStateBase>(_clientType, "State");
            _queue = Field<FastPriorityQueue<MyStateDataEntry>>(_clientType, "DirtyQueue");
            _layers = Field<IDictionary>(_clientType, "ReplicableToLayer");
            var layerType = _clientType.GetNestedType("UpdateLayer", instance) ?? throw new TypeLoadException("MyClient.UpdateLayer");
            var layer = Expression.Parameter(typeof(object));
            var descriptor = Expression.Field(Expression.Convert(layer, layerType), "Descriptor");
            _layerRadius = Expression.Lambda<Func<object, int>>(Expression.Field(descriptor, "Radius"), layer).Compile();
            var priority = typeof(MyStateDataEntry).GetField("Priority", instance)
                           ?? typeof(FastPriorityQueue<MyStateDataEntry>.Node).GetField("Priority", instance)
                           ?? throw new MissingFieldException("MyStateDataEntry", "Priority");
            var entry = Expression.Parameter(typeof(MyStateDataEntry));
            _priority = Expression.Lambda<Func<MyStateDataEntry, long>>(Expression.Field(entry, priority), entry).Compile();
            var schedule = typeof(MyReplicationServer).GetMethod("ScheduleStateGroupSync", instance) ?? throw new MissingMethodException("MyReplicationServer", "ScheduleStateGroupSync");
            var pattern = ctx.GetPattern(schedule);
            pattern.Prefixes.Add(typeof(StateSyncSlowdown).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
            pattern.Suffixes.Add(typeof(StateSyncSlowdown).GetMethod(nameof(Suffix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static Func<object, T> Field<T>(Type type, string name)
        {
            var self = Expression.Parameter(typeof(object));
            return Expression.Lambda<Func<object, T>>(Expression.Convert(Expression.Field(Expression.Convert(self, type), name), typeof(T)), self).Compile();
        }

        private static bool Enabled()
        {
            if (!PeakLoad.Active) return false;
            var config = SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config;
            return config != null && config.SlowStateSync;
        }

        private static void Prefix(object client, MyStateDataEntry groupEntry)
        {
            _wasQueued = true;
            if (!Enabled()) return;
            try
            {
                _wasQueued = _queue(client).Contains(groupEntry);
            }
            catch (Exception)
            {
                _wasQueued = true;
            }
        }

        private static void Suffix(object client, MyStateDataEntry groupEntry, long currentTime)
        {
            if (_wasQueued || !Enabled()) return;
            _wasQueued = true;
            try
            {
                var queue = _queue(client);
                if (groupEntry?.Owner == null || groupEntry.Group == null || groupEntry.Group.IsStreaming || !queue.Contains(groupEntry)) return;
                var root = groupEntry.Owner.GetParent() ?? groupEntry.Owner;
                var state = _state(client);
                if (state == null || ReferenceEquals(root, state.ControlledReplicable) || ReferenceEquals(root, state.CharacterReplicable)) return;
                var layers = _layers(client);
                if (layers == null || !layers.Contains(root)) return;
                var layer = layers[root];
                if (layer == null || _layerRadius(layer) < FarRadiusM) return;
                var due = _priority(groupEntry);
                var delay = due - currentTime;
                if (delay <= 0) return;
                queue.UpdatePriority(groupEntry, due + delay);
                Interlocked.Increment(ref Held);
            }
            catch (Exception)
            {
            }
        }
    }
}
