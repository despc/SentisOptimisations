using System;
using System.Reflection;
using System.Threading;

namespace SentisOptimisations
{
    /// <summary>
    /// Whether blocks' updates may reach the plugin from several threads at once: only beside SentisClusters.
    ///
    /// The plugin is written for the game thread, so its caches and budgets are plain dictionaries and counters.
    /// SentisClusters updates the entities of different clusters of the physics on different threads, so beside it
    /// the plugin's shared state is locked, its counters are atomic and its scratch collections are each thread's own.
    /// All of that goes through this class and is switched on only when the SentisClusters assembly is loaded into the
    /// server. Without it nothing is locked, and the code runs as it did before.
    ///
    /// <code>using (ClusterThreads.Lock(dictionary)) { ... }</code>
    /// </summary>
    public static class ClusterThreads
    {
        private const string ClustersAssembly = "SentisClusters";

        /// <summary>SentisClusters is loaded: the plugin's code may run on several threads at once.</summary>
        public static bool On { get; private set; }

        static ClusterThreads()
        {
            AppDomain.CurrentDomain.AssemblyLoad += (_, e) => Look(e.LoadedAssembly);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) Look(assembly);
        }

        /// <summary>From the plugin's Init: looks at the assemblies loaded so far and listens for the rest.</summary>
        public static void Init()
        {
        }

        private static void Look(Assembly assembly)
        {
            if (On) return;
            string name;
            try { name = assembly.GetName().Name; }
            catch (Exception) { return; }
            if (!string.Equals(name, ClustersAssembly, StringComparison.OrdinalIgnoreCase)) return;
            On = true;
            Report();
        }

        private static void Report() =>
            SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Info(
                "SentisOptimisations: SentisClusters is loaded: shared state of welders, drills, refineries and grid snapshots is locked");

        /// <summary>The object's lock beside SentisClusters; nothing without it.</summary>
        public static Token Lock(object o)
        {
            if (!On) return default(Token);
            Monitor.Enter(o);
            return new Token(o);
        }

        /// <summary>
        /// The lock of the welders' shared state, taken only while the game runs updates off its thread
        /// (<c>MyEntities.IsAsyncUpdateInProgress</c>) beside SentisClusters. Reentrant.
        /// </summary>
        public static Token Gate()
        {
            if (!On || !Sandbox.Game.Entities.MyEntities.IsAsyncUpdateInProgress) return default(Token);
            Monitor.Enter(GateLock);
            return new Token(GateLock);
        }

        private static readonly object GateLock = new object();

        public static void Increment(ref int counter)
        {
            if (On) Interlocked.Increment(ref counter);
            else counter++;
        }

        public static void Increment(ref long counter)
        {
            if (On) Interlocked.Increment(ref counter);
            else counter++;
        }

        /// <summary>
        /// A scratch object: one for the game thread as it always was, each thread's own beside SentisClusters.
        /// </summary>
        public sealed class PerThread<T> where T : class, new()
        {
            private readonly T _one = new T();
            private ThreadLocal<T> _own;

            public T Value
            {
                get
                {
                    if (!On) return _one;
                    var own = _own;
                    if (own == null)
                    {
                        Interlocked.CompareExchange(ref _own, new ThreadLocal<T>(() => new T()), null);
                        own = _own;
                    }
                    return own.Value;
                }
            }
        }

        public struct Token : IDisposable
        {
            private object _taken;

            internal Token(object taken) => _taken = taken;

            public void Dispose()
            {
                if (_taken == null) return;
                Monitor.Exit(_taken);
                _taken = null;
            }
        }
    }
}
