using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace SentisOptimisations.Tests
{
    /// <summary>
    /// Makes the plugin usable outside a running server: a live Config and a non-null Instance.
    /// </summary>
    internal static class PluginHarness
    {
        public static readonly Assembly[] PluginAssemblies =
        {
            TestPaths.PluginAssembly,
            Assembly.Load("SentisGameplayImprovements"),
            Assembly.Load("SentisAdventures"),
        };

        public static readonly Type PluginType =
            typeof(SentisOptimisationsPlugin.SentisOptimisationsPlugin);

        static PluginHarness()
        {
            foreach (var asm in PluginAssemblies)
                PrimePlugin(FindPluginType(asm));
        }

        public static Type FindPluginType(Assembly asm) => asm.GetTypes()
            .First(t => !t.IsAbstract && typeof(Torch.TorchPluginBase).IsAssignableFrom(t));

        static void PrimePlugin(Type pluginType)
        {
            SetStaticField(pluginType, "Instance", FormatterServices.GetUninitializedObject(pluginType));

            var configField = pluginType.GetField("_config", BindingFlags.Static | BindingFlags.NonPublic);
            if (configField == null) return;
            var persistentType = configField.FieldType;
            var configType = persistentType.GenericTypeArguments.Single();
            var persistent = FormatterServices.GetUninitializedObject(persistentType);
            var config = Activator.CreateInstance(configType);
            var dataProp = persistentType.GetProperty("Data");
            var setter = dataProp?.GetSetMethod(true);
            if (setter != null)
                setter.Invoke(persistent, new[] { config });
            else
                persistentType.GetField("<Data>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(persistent, config);
            configField.SetValue(null, persistent);
        }

        public static void Ensure() { }

        public static object Config => PluginType.GetProperty("Config").GetValue(null);

        public static void SetConfig(string name, object value) =>
            SetConfig(PluginType, name, value);

        public static void SetConfig(Type pluginType, string name, object value)
        {
            var cfg = pluginType.GetProperty("Config").GetValue(null);
            var prop = cfg.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(prop != null && prop.CanWrite, $"config property {name} not writable");
            prop.SetValue(cfg, value, null);
        }

        static void SetStaticField(Type t, string name, object value)
        {
            var field = t.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                      ?? t.GetField($"<{name}>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(null, value);
        }
    }

    public static class AllBindings
    {
        public struct BindingRow
        {
            public Type Shim;
            public MethodBase Original;
            public MethodInfo Patch;
            public bool IsPrefix;
            public override string ToString() =>
                $"{Shim.Name} -> {TorchHarness.Describe(Original)} [{(IsPrefix ? "prefix" : "suffix")} {Patch.Name}]";
        }

        static List<BindingRow> _rows;
        static readonly object BuildLock = new object();

        public static List<BindingRow> Rows
        {
            get
            {
                lock (BuildLock)
                {
                    // (re)build whenever empty: a build produced while other tests mutate
                    // global plugin state is not trustworthy and must not be cached.
                    if (_rows == null || _rows.Count == 0)
                        _rows = Build();
                }
                return _rows;
            }
        }

        static List<BindingRow> Build()
        {
            PluginHarness.Ensure();
            var rows = new List<BindingRow>();
            foreach (var shim in Shims())
            {
                var ctx = TorchHarness.NewContext(); // also resets Torch's process-wide pattern registry
                TorchHarness.RegisterShim(ctx, shim);
                foreach (var (orig, pattern) in TorchHarness.GetPatterns(ctx))
                {
                    foreach (var p in TorchHarness.Prefixes(pattern))
                        rows.Add(new BindingRow { Shim = shim, Original = orig, Patch = p, IsPrefix = true });
                    foreach (var s in TorchHarness.Suffixes(pattern))
                        rows.Add(new BindingRow { Shim = shim, Original = orig, Patch = s, IsPrefix = false });
                }
            }
            return rows;
        }

        public static string ShimsDiagnostics()
        {
            var attrType = typeof(Torch.Managers.PatchManager.PatchShimAttribute);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"attr identity: {attrType.Assembly.GetName().Name}#{attrType.Assembly.GetHashCode():X} @ {attrType.Assembly.Location}");
            foreach (var a in PluginHarness.PluginAssemblies)
            {
                int total = 0, withAttr = 0, withAnyPatchShim = 0;
                foreach (var t in a.GetTypes())
                {
                    if (!t.IsClass) continue;
                    total++;
                    if (t.GetCustomAttributes(attrType, false).Any()) withAttr++;
                    if (t.GetCustomAttributes(false).Any(x => x.GetType().Name == "PatchShimAttribute")) withAnyPatchShim++;
                }
                sb.AppendLine($"{a.GetName().Name}#{a.GetHashCode():X}: classes={total} exactAttr={withAttr} anyNamedPatchShim={withAnyPatchShim} @ {a.Location}");
            }
            var torches = AppDomain.CurrentDomain.GetAssemblies()
                .Where(x => x.GetName().Name == "Torch")
                .Select(x => $"#{x.GetHashCode():X} {x.Location}");
            sb.AppendLine("loaded Torch copies: " + string.Join(" | ", torches));
            return sb.ToString();
        }

        public static List<Type> Shims()
        {
            var attrType = typeof(Torch.Managers.PatchManager.PatchShimAttribute);
            return PluginHarness.PluginAssemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => t.IsClass && t.GetCustomAttributes(attrType, false).Any())
                .OrderBy(t => t.FullName)
                .ToList();
        }
    }

    public class ValidatorSelfTests
    {
        class Target
        {
            private int hidden = 1; // Torch '___hidden' trims leading underscores: binds to field 'hidden'
            public int InstanceMethod(int alpha, string beta) => alpha;
            public void VoidMethod(int alpha) { }
            public static int StaticMethod(int alpha) => alpha;
        }

        // valid
        static bool PrefixOk(Target __instance, int alpha, string beta) => true;
        static void SuffixOk(int alpha, ref int __result) { }
        static bool PrefixHiddenField(ref int ___hidden) => true;

        // invalid: __result on void target
        static bool PrefixResultOnVoid(ref int __result) => true;
        // invalid: unknown parameter name
        static bool PrefixBadName(int alphaX) => true;
        // invalid: wrong param type
        static bool PrefixBadType(string alpha) => true;
        // invalid: __instance on static target
        static bool PrefixInstanceOnStatic(Target __instance) => true;
        // invalid: missing private field
        static bool PrefixMissingField(ref int ___nope) => true;
        // invalid: prefix returning int
        static int PrefixBadReturn() => 1;

        [Fact]
        public void Valid_binding_passes()
        {
            var orig = typeof(Target).GetMethod("InstanceMethod");
            var patch = typeof(ValidatorSelfTests).GetMethod("PrefixOk", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.Empty(PatchBindingValidator.Validate(orig, patch, true));
        }

        [Fact]
        public void Valid_suffix_passes()
        {
            var orig = typeof(Target).GetMethod("InstanceMethod");
            var patch = typeof(ValidatorSelfTests).GetMethod("SuffixOk", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.Empty(PatchBindingValidator.Validate(orig, patch, false));
        }

        [Fact]
        public void Field_injection_passes()
        {
            var orig = typeof(Target).GetMethod("InstanceMethod");
            var patch = typeof(ValidatorSelfTests).GetMethod("PrefixHiddenField", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.Empty(PatchBindingValidator.Validate(orig, patch, true));
        }

        [Fact]
        public void Result_on_void_target_fails()
        {
            var orig = typeof(Target).GetMethod("VoidMethod");
            var patch = typeof(ValidatorSelfTests).GetMethod("PrefixResultOnVoid", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.Contains(PatchBindingValidator.Validate(orig, patch, true), e => e.Contains("__result"));
        }

        [Fact]
        public void Unknown_parameter_name_fails()
        {
            var orig = typeof(Target).GetMethod("InstanceMethod");
            var patch = typeof(ValidatorSelfTests).GetMethod("PrefixBadName", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.Contains(PatchBindingValidator.Validate(orig, patch, true), e => e.Contains("alphaX"));
        }

        [Fact]
        public void Wrong_parameter_type_fails()
        {
            var orig = typeof(Target).GetMethod("InstanceMethod");
            var patch = typeof(ValidatorSelfTests).GetMethod("PrefixBadType", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.Contains(PatchBindingValidator.Validate(orig, patch, true), e => e.Contains("type"));
        }

        [Fact]
        public void Instance_on_static_target_fails()
        {
            var orig = typeof(Target).GetMethod("StaticMethod");
            var patch = typeof(ValidatorSelfTests).GetMethod("PrefixInstanceOnStatic", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.Contains(PatchBindingValidator.Validate(orig, patch, true), e => e.Contains("__instance"));
        }

        [Fact]
        public void Missing_private_field_fails()
        {
            var orig = typeof(Target).GetMethod("InstanceMethod");
            var patch = typeof(ValidatorSelfTests).GetMethod("PrefixMissingField", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.Contains(PatchBindingValidator.Validate(orig, patch, true), e => e.Contains("nope"));
        }

        [Fact]
        public void Bad_prefix_return_type_fails()
        {
            var orig = typeof(Target).GetMethod("InstanceMethod");
            var patch = typeof(ValidatorSelfTests).GetMethod("PrefixBadReturn", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.Contains(PatchBindingValidator.Validate(orig, patch, true), e => e.Contains("return"));
        }
    }
}
