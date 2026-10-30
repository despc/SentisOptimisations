using System;
using System.Threading;

namespace SentisOptimisations
{
    /// <summary>
    /// Harmony's lock of its shared state: a named mutex, "HarmonySharedState", one for every process of the machine's
    /// session - every Torch there with Harmony waits on the same one. A server killed while it held it (a patch going in
    /// at its start) leaves it abandoned, and the next wait in any server throws AbandonedMutexException; Harmony then
    /// holds it on that thread without letting it go (its WaitOne threw before its finally could know it had it), so the
    /// thread's end abandons it again for the next. A chain: patches failing in server after server, restarts and all
    /// (prod, 30.10.2026: SentisClusters' Detach, SGI's ExplosionsPatch and the anticheat's spawn check skipped), and
    /// while that thread lives every other server's patching waits on it for good.
    /// </summary>
    internal static class HarmonyMutex
    {
        private const string Name = "HarmonySharedState";

        /// <summary>
        /// The mutex let go when it is abandoned or held by this thread (Harmony left it so); a mutex another live thread
        /// holds is left alone, not waited for. Returns what it found, null when it was fine.
        /// </summary>
        public static string Heal()
        {
            try
            {
                using (var mutex = new Mutex(false, Name))
                {
                    bool owned, abandoned = false;
                    try { owned = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { owned = abandoned = true; }
                    if (!owned) return null;
                    var held = 0;
                    while (true)
                    {
                        try { mutex.ReleaseMutex(); held++; }
                        catch (ApplicationException) { break; }
                    }
                    if (abandoned) return "Harmony's mutex was abandoned (a server died while patching): let go";
                    return held > 1 ? "Harmony's mutex was held by this thread " + (held - 1) + " time(s) over: let go" : null;
                }
            }
            catch (Exception e)
            {
                return "Harmony's mutex not looked at: " + e.Message;
            }
        }

        /// <summary>Whether the exception or one inside it is an abandoned wait on the mutex.</summary>
        public static bool IsAbandoned(Exception e)
        {
            for (; e != null; e = e.InnerException)
                if (e is AbandonedMutexException) return true;
            return false;
        }
    }
}
