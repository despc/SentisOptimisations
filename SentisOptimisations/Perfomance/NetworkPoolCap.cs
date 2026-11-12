using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using VRage.Collections;

namespace SentisOptimisations.Perfomance
{
    /// <summary>
    /// A packet or a buffer given back to the game's network pools no longer counts every one pooled before it.
    ///
    /// <c>MyConcurrentBucketPool&lt;T&gt;.Return</c> asks its stack of pooled items for <c>Count</c> before pushing, to
    /// keep under a maximum; a <c>ConcurrentStack</c>'s <c>Count</c> walks the whole stack. The pools of the received
    /// packets (<c>MyReceiveQueue.m_messagePool</c>) and of the bit streams (<c>BitStream.BufferPool</c>) have no maximum
    /// (<c>int.MaxValue</c>): after a burst of packets thousands are pooled, and every packet given back (each client's
    /// acks, updates, events) walks them all - 1.85 s of the game thread in a minute's trace of the live server at its
    /// peak (11.11.2026). The method is not patched (a generic class' method); the pools are given a maximum of
    /// <see cref="Cap"/> items of a size: a count walks at most that many, an item over it is freed (as the game frees
    /// one over any pool's maximum), and a burst that needs more gets new ones.
    /// </summary>
    public static class NetworkPoolCap
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public const int Cap = 512;

        public static readonly (string Type, string Field)[] Pools =
        {
            ("Sandbox.Engine.Networking.MyReceiveQueue, Sandbox.Game", "m_messagePool"),
            ("VRage.Library.Collections.BitStream, VRage.Library", "BufferPool"),
        };

        public static readonly List<string> Capped = new List<string>();

        public static void Apply()
        {
            foreach (var (typeName, fieldName) in Pools)
            {
                try
                {
                    var pool = Type.GetType(typeName, false)?.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null);
                    if (pool != null && SetCap(pool)) Capped.Add(typeName.Split(',')[0] + "." + fieldName);
                    else Log.Warn("Network pool cap: " + typeName + "." + fieldName + " is not a pool this knows, left as it is");
                }
                catch (Exception e)
                {
                    Log.Error(e, "Network pool cap: " + typeName + "." + fieldName);
                }
            }
        }

        /// <summary>The pool's maximum made <see cref="Cap"/> if it had none; false if it is not a pool this knows.</summary>
        public static bool SetCap(object pool)
        {
            for (var type = pool.GetType(); type != null; type = type.BaseType)
            {
                if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(MyConcurrentBucketPool<>)) continue;
                var max = type.GetField("m_maxPooledInstances", BindingFlags.Instance | BindingFlags.NonPublic);
                if (max == null || max.FieldType != typeof(int)) return false;
                if ((int)max.GetValue(pool) == int.MaxValue) max.SetValue(pool, Cap);
                return true;
            }
            return false;
        }
    }
}
