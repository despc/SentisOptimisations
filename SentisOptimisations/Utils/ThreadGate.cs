using System;
using System.Threading;
using Sandbox.Game.Entities;

namespace SentisOptimisations
{
    /// <summary>
    /// One lock for the plugin's shared state that blocks' updates reach.
    ///
    /// The plugin was written for the game thread: its caches and budgets are plain dictionaries and counters. A
    /// plugin that runs the entities' updates of different clusters of the physics on different threads
    /// (SentisClusters) brings several blocks' updates into those at once. While the game runs updates off its
    /// thread it says so (<c>MyEntities.IsAsyncUpdateInProgress</c>): then the gate is taken, and the code under it
    /// runs one thread at a time. Any other time - the game thread's own loops, a server without such a plugin - it
    /// is not taken at all, and nothing changes.
    ///
    /// <code>using (ThreadGate.Enter()) { ... }</code>
    /// The lock is reentrant: code under the gate may call code that takes it again.
    /// </summary>
    public static class ThreadGate
    {
        private static readonly object Lock = new object();

        public struct Token : IDisposable
        {
            private bool _taken;

            internal Token(bool taken) => _taken = taken;

            public void Dispose()
            {
                if (!_taken) return;
                _taken = false;
                Monitor.Exit(Lock);
            }
        }

        public static Token Enter()
        {
            if (!MyEntities.IsAsyncUpdateInProgress) return default(Token);
            Monitor.Enter(Lock);
            return new Token(true);
        }

        /// <summary>Whether the plugin's code may now run on several threads at once.</summary>
        public static bool Shared => MyEntities.IsAsyncUpdateInProgress;
    }
}
