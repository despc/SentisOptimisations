using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using NLog;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The plugins' code compiled on a background thread once the world is loaded, not in the frame that first runs it.
    ///
    /// The game's own assemblies are native images (NGen) and cost nothing the first time; the plugins are plain IL, and
    /// every method is compiled by the JIT the first time it is called - on the game thread, inside a frame. A bot's big
    /// methods (placing a block, building a rig) cost 10-20 ms the first time after each start. Here every method of the
    /// plugins' assemblies is prepared (<c>RuntimeHelpers.PrepareMethod</c>) on a thread of its own at low priority; a
    /// method compiled already, or patched (compiled by the patch manager), is left as it is.
    /// </summary>
    public static class PluginJitWarmup
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static int _started;

        /// <summary>The assemblies prepared: the plugins', and Torch's.</summary>
        public static bool Wanted(string assemblyName) =>
            assemblyName.StartsWith("Sentis") || assemblyName.StartsWith("VirtualGarage") || assemblyName.StartsWith("Torch") ||
            assemblyName.StartsWith("Essentials");

        public static void Run()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            var thread = new Thread(Prepare) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "PluginJitWarmup" };
            thread.Start();
        }

        private static void Prepare()
        {
            var started = Stopwatch.GetTimestamp();
            int prepared = 0, failed = 0;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || !Wanted(assembly.GetName().Name)) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (var type in types)
                {
                    if (type.ContainsGenericParameters) continue;
                    MethodBase[] methods;
                    try
                    {
                        methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                            .Cast<MethodBase>()
                            .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                            .ToArray();
                    }
                    catch { continue; }
                    foreach (var method in methods)
                    {
                        if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
                        try
                        {
                            RuntimeHelpers.PrepareMethod(method.MethodHandle);
                            prepared++;
                        }
                        catch
                        {
                            failed++;
                        }
                    }
                }
            }
            if (global::SentisOptimisations.DiagLog.On) Log.Info($"Plugin JIT warm-up: {prepared} methods compiled in the background in {(Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency:F0} ms ({failed} could not be)");
        }
    }
}
