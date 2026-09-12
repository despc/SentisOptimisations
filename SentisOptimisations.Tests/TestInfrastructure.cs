using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SentisOptimisations.Tests
{
    internal static class TestPaths
    {
        public static string SeRoot => Environment.GetEnvironmentVariable("SE_ROOT") ?? @"C:\SE";
        public static string DedicatedServer => Path.Combine(SeRoot, "DedicatedServer64");
        public static string PluginDir => Path.Combine(SeRoot, "Plugins", "SentisOptimisations");

        public static readonly string[] ProbeDirs =
        {
            AppDomain.CurrentDomain.BaseDirectory,
            SeRoot,
            DedicatedServer,
            PluginDir,
        };

        public static Assembly PluginAssembly => _pluginAssembly ??= LoadPlugin();

        static Assembly LoadPlugin()
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations");
            if (loaded != null) return loaded;
            try { return Assembly.Load("SentisOptimisations"); }
            catch { return Assembly.LoadFrom(Path.Combine(PluginDir, "SentisOptimisations.dll")); }
        }
        static Assembly _pluginAssembly;
    }

    /// <summary>Captures NLog errors during a scope so guarded failures are visible to tests.</summary>
    internal sealed class NLogCapture : IDisposable
    {
        readonly MemoryTarget _target = new MemoryTarget { Layout = "${level}|${message}|${exception:format=ToString}" };
        readonly LoggingRule _rule;

        public NLogCapture()
        {
            var cfg = LogManager.Configuration ?? new LoggingConfiguration();
            cfg.AddTarget("testcap", _target);
            _rule = new LoggingRule("*", LogLevel.Warn, _target);
            cfg.LoggingRules.Add(_rule);
            LogManager.Configuration = cfg;
        }

        public IEnumerable<string> Entries => _target.Logs;

        public IEnumerable<string> Errors => _target.Logs.Where(l => l.StartsWith("Error") || l.StartsWith("Fatal"));

        public void Dispose()
        {
            var cfg = LogManager.Configuration;
            cfg?.LoggingRules.Remove(_rule);
            if (cfg != null) LogManager.Configuration = cfg;
        }
    }

    /// <summary>Creates a real Torch PatchContext without running the server, and reads back registered patterns.</summary>
    internal static class TorchHarness
    {
        static readonly Type CtxType = typeof(Torch.Managers.PatchManager.PatchContext);
        static readonly FieldInfo PatternsField = CtxType.GetField("_rewritePatterns",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly ConstructorInfo Ctor = CtxType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length == 0);

        static TorchHarness()
        {
            // Touch TorchBase so its static plumbing (logger etc.) exists.
            _ = typeof(Torch.TorchBase).Assembly;
        }

        static readonly Type PatchManagerType = typeof(Torch.Managers.PatchManager.PatchManager);
        static readonly FieldInfo GlobalPatterns = PatchManagerType.GetField("_rewritePatterns",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>
        /// Torch keeps a PROCESS-WIDE pattern registry: MethodRewriteSet.Add returns false for a
        /// (method, patch) pair already registered globally, so a second registration of the same
        /// shim silently produces empty local patterns. Tests must reset the global registry first.
        /// </summary>
        public static void ResetGlobalRegistry()
        {
            var dict = GlobalPatterns.GetValue(null) as IDictionary;
            dict?.Clear();
        }

        public static object NewContext()
        {
            ResetGlobalRegistry();
            return Ctor.Invoke(null);
        }

        public static void RegisterShim(object ctx, Type shim)
        {
            // Prefer the unguarded PatchImpl (if present) so failures throw instead of being logged.
            var impl = shim.GetMethod("PatchImpl", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { CtxType }, null);
            var patch = impl ?? shim.GetMethod("Patch", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { CtxType }, null);
            Assert.NotNull(patch);
            try
            {
                patch.Invoke(null, new[] { ctx });
            }
            catch (TargetInvocationException tie)
            {
                throw new ShimRegistrationException(shim.FullName, tie.InnerException);
            }
        }

        public static List<(MethodBase Original, object Pattern)> GetPatterns(object ctx)
        {
            var dict = PatternsField.GetValue(ctx) as IDictionary;
            var list = new List<(MethodBase, object)>();
            foreach (DictionaryEntry e in dict)
                list.Add(((MethodBase)e.Key, e.Value));
            return list;
        }

        public static List<MethodInfo> Prefixes(object pattern) => RewriteSet(pattern, "Prefixes");
        public static List<MethodInfo> Suffixes(object pattern) => RewriteSet(pattern, "Suffixes");

        static List<MethodInfo> RewriteSet(object pattern, string name)
        {
            var prop = pattern.GetType().GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop == null) return new List<MethodInfo>();
            var set = prop.GetValue(pattern);
            if (set == null) return new List<MethodInfo>();
            var enumerable = set as IEnumerable;
            if (enumerable == null)
            {
                var getEnum = set.GetType().GetMethod("GetEnumerator", Type.EmptyTypes);
                var iter = (IEnumerator)getEnum.Invoke(set, null);
                var res = new List<MethodInfo>();
                while (iter.MoveNext()) res.Add((MethodInfo)iter.Current);
                return res;
            }
            return enumerable.Cast<object>().Cast<MethodInfo>().ToList();
        }

        public static string Describe(MethodBase m)
        {
            if (m == null) return "<null>";
            var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
            return $"{m.DeclaringType?.FullName}.{m.Name}({ps})";
        }
    }

    internal class ShimRegistrationException : Exception
    {
        public ShimRegistrationException(string shim, Exception inner)
            : base($"Patch registration threw for shim '{shim}': {inner}", inner) { }
    }

    /// <summary>
    /// Validates a prefix/suffix MethodInfo against the target it is registered on,
    /// replicating Torch.Managers.PatchManager.DecoratedMethod's binding rules:
    /// parameters bind BY NAME; specials are __instance/__result/__original/__prefixSkipped;
    /// '___x' binds to a (private) field of the target type. A parameter name that matches
    /// neither special nor target parameter throws KeyNotFound at Torch apply time.
    /// </summary>
    internal static class PatchBindingValidator
    {
        const string Instance = "__instance";
        const string Result = "__result";
        const string Original = "__original";
        const string PrefixSkipped = "__prefixSkipped";

        public static List<string> Validate(MethodBase original, MethodInfo patch, bool isPrefix)
        {
            var errors = new List<string>();
            if (original == null) { errors.Add("target method is null"); return errors; }

            if (isPrefix)
            {
                if (patch.ReturnType != typeof(bool) && patch.ReturnType != typeof(void))
                    errors.Add($"prefix must return bool or void, returns {patch.ReturnType.Name}");
            }
            else
            {
                if (patch.ReturnType != typeof(void))
                    errors.Add($"suffix must return void, returns {patch.ReturnType.Name}");
            }

            var targetParams = original.GetParameters();

            foreach (var p in patch.GetParameters())
            {
                var name = p.Name ?? "";
                if (name == Instance)
                {
                    if (original.IsStatic)
                        errors.Add($"{patch.Name}: __instance on a static target");
                    else if (!p.ParameterType.IsByRef &&
                             !patchedTypeMatchesTarget(p.ParameterType, original.GetParameterOrDeclaringType()))
                        errors.Add($"{patch.Name}: __instance type {p.ParameterType.Name} not assignable from {original.DeclaringType?.Name}");
                    continue;
                }
                if (name == Result)
                {
                    var m = original as MethodInfo;
                    if (m == null || m.ReturnType == typeof(void))
                        errors.Add($"{patch.Name}: __result on a void-returning target (KeyNotFound at apply)");
                    else if (p.ParameterType.IsByRef)
                    {
                        var el = p.ParameterType.GetElementType();
                        if (!patchedTypeMatchesTarget(el, m.ReturnType))
                            errors.Add($"{patch.Name}: ref __result type {el?.Name} != target return {m.ReturnType.Name}");
                    }
                    else if (!patchedTypeMatchesTarget(p.ParameterType, m.ReturnType))
                        errors.Add($"{patch.Name}: __result type {p.ParameterType.Name} != target return {m.ReturnType.Name}");
                    continue;
                }
                if (name == Original)
                {
                    if (!typeof(MethodBase).IsAssignableFrom(p.ParameterType))
                        errors.Add($"{patch.Name}: __original must be MethodBase-compatible");
                    continue;
                }
                if (name == PrefixSkipped)
                {
                    if (p.ParameterType != typeof(bool))
                        errors.Add($"{patch.Name}: __prefixSkipped must be bool");
                    continue;
                }
                if (name.StartsWith("___"))
                {
                    var fieldName = name.Substring(3);
                    if (!FieldExists(original.DeclaringType, fieldName))
                        errors.Add($"{patch.Name}: field '{fieldName}' (from {name}) not found on {original.DeclaringType?.Name} or bases");
                    continue;
                }

                var match = targetParams.FirstOrDefault(tp => tp.Name == name);
                if (match == null)
                {
                    var known = string.Join(",", targetParams.Select(tp => tp.Name));
                    errors.Add($"{patch.Name}: parameter '{name}' does not bind to any target parameter (available: [{known}]) — KeyNotFound at apply");
                    continue;
                }
                if (!paramTypesMatch(p.ParameterType, match.ParameterType))
                    errors.Add($"{patch.Name}: parameter '{name}' type {p.ParameterType.Name} != target {match.ParameterType.Name}");
            }
            return errors;
        }

        static Type GetParameterOrDeclaringType(this MethodBase m) => m.DeclaringType;

        static bool paramTypesMatch(Type patchT, Type targetT)
        {
            if (patchT == targetT) return true;
            // Torch tolerates ref-mismatch in both directions (prefixes may take value params
            // by ref and pass by-ref target params by value) — only the element type must match.
            var a = patchT.IsByRef ? patchT.GetElementType() : patchT;
            var b = targetT.IsByRef ? targetT.GetElementType() : targetT;
            // ref-ness mismatch is allowed both ways (Harmony-compatible: prefixes may take
            // value params by ref; suffix __result by ref on value return). Element must match.
            return patchedTypeMatchesTarget(a, b);
        }

        /// <summary>Torch passes raw values; we accept exact type or a base/interface of the target type
        /// (Torch emits untyped ldarg so base types load fine), reject unrelated.</summary>
        static bool patchedTypeMatchesTarget(Type patchT, Type targetT)
        {
            if (patchT == null || targetT == null) return false;
            if (patchT == targetT) return true;
            if (patchT == typeof(object)) return true;
            return patchT.IsAssignableFrom(targetT);
        }

        static readonly Dictionary<Type, HashSet<string>> FieldCache = new Dictionary<Type, HashSet<string>>();

        public static bool FieldExists(Type type, string fieldName)
        {
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                var names = FieldCache.GetOrAdd(t, tt => new HashSet<string>(
                    tt.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                 BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Select(f => f.Name)));
                if (names.Contains(fieldName)) return true;
            }
            return false;
        }
    }

    static class DictExt
    {
        public static TV GetOrAdd<TV, TK>(this Dictionary<TK, TV> d, TK k, Func<TK, TV> f)
        {
            if (d.TryGetValue(k, out var v)) return v;
            v = f(k);
            d[k] = v;
            return v;
        }
    }
}
