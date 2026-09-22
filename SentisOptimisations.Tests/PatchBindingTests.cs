using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace SentisOptimisations.Tests
{
    /// <summary>
    /// The anti-regression core: every [PatchShim] in the plugin must register cleanly against
    /// the CURRENT game/Torch DLLs, and every registered prefix/suffix must satisfy Torch's
    /// name-based parameter binding. This catches: moved/renamed targets, __result on void,
    /// renamed target parameters (KeyNotFound at Torch apply time), vanished private fields.
    /// </summary>
    public class PatchBindingTests
    {
        readonly ITestOutputHelper _out;
        public PatchBindingTests(ITestOutputHelper output) { _out = output; }

        [Fact]
        public void Shims_are_discovered()
        {
            var shims = AllBindings.Shims();
            _out.WriteLine($"shims: {shims.Count}");
            Assert.True(shims.Count >= 35, $"expected the whole shim set, found {shims.Count}: " +
                string.Join(",", shims.Select(s => s.Name)));
        }

        [Fact]
        public void Every_shim_registers_without_throwing()
        {
            PluginHarness.Ensure();
            var failures = new List<string>();
            foreach (var shim in AllBindings.Shims())
            {
                try
                {
                    var ctx = TorchHarness.NewContext();
                    TorchHarness.RegisterShim(ctx, shim);
                }
                catch (Exception e)
                {
                    failures.Add($"{shim.Name}: {e.GetBaseException().Message}");
                }
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Fact]
        public void Every_binding_is_valid()
        {
            var errors = new List<string>();
            foreach (var r in AllBindings.Rows)
            {
                var errs = PatchBindingValidator.Validate(r.Original, r.Patch, r.IsPrefix);
                foreach (var e in errs)
                    errors.Add($"{r}\n    {e}");
            }
            _out.WriteLine($"validated {AllBindings.Rows.Count} bindings");
            Assert.True(errors.Count == 0, string.Join("\n", errors));
        }

        [Fact]
        public void Bindings_are_not_empty()
        {
            // guards the harness itself: if discovery silently returns nothing the suite is worthless
            Assert.NotEmpty(AllBindings.Rows);
        }

        [Fact]
        public void No_shim_is_applied_to_null_method()
        {
            Assert.DoesNotContain(AllBindings.Rows, r => r.Original == null);
        }

        [Fact]
        public void All_patch_shims_are_static_classes()
        {
            // Torch's PatchManager logs "isn't declared singleton" for anything but sealed+abstract
            var bad = AllBindings.Shims()
                .Where(t => !(t.IsSealed && t.IsAbstract))
                .Select(t => t.FullName).ToList();
            Assert.True(bad.Count == 0, "non-static shim classes: " + string.Join(",", bad));
        }
    }

    /// <summary>Pins the specific production regressions that were fixed, so they cannot come back.</summary>
    public class RegressionPinTests
    {
        static IEnumerable<AllBindings.BindingRow> RowsFor(string methodName) =>
            AllBindings.Rows.Where(r => r.Original?.Name == methodName);

        [Fact]
        public void ApplyVolumetricExplosion_prefix_has_no_result_and_binds_real_parameters()
        {
            // was: 'ref bool __result' on a void method → KeyNotFound FATAL at startup
            var rows = RowsFor("ApplyVolumetricExplosion").ToList();
            Assert.NotEmpty(rows);
            foreach (var r in rows)
            {
                var ps = r.Patch.GetParameters();
                Assert.DoesNotContain(ps, p => p.Name == "__result");
                foreach (var p in ps)
                {
                    if (p.Name == "__instance") continue;
                    Assert.Contains(r.Original.GetParameters(), tp => tp.Name == p.Name);
                }
            }
        }

        [Fact]
        public void Contract_hauling_reward_target_is_static_with_exact_param_names()
        {
            // was: instance patch on a STATIC method + 'baseRew' vs 'baseReward' → KeyNotFound. The suffix now
            // scales only the result, so it names no parameter of the target at all.
            var rows = RowsFor("GetHaulingMoneyReward").ToList();
            Assert.NotEmpty(rows);
            foreach (var r in rows)
            {
                Assert.True(r.Original.IsStatic, "GetHaulingMoneyReward must be patched as static");
                Assert.Equal(new[] { "__result" }, r.Patch.GetParameters().Select(p => p.Name));
            }
        }

        [Fact]
        public void Replicables_patches_every_CalculateLayerOfReplicable_overload_in_game()
        {
            // game currently has 2 overloads; if a third appears, this pins that we notice
            var myClient = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("VRage.Network.MyClient")).FirstOrDefault(x => x != null);
            Assert.NotNull(myClient);
            var gameOverloads = myClient.GetMethods(
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == "CalculateLayerOfReplicable").Select(m => m.GetParameters().Length).OrderBy(x => x).ToList();
            var patched = RowsFor("CalculateLayerOfReplicable")
                .Select(r => r.Original.GetParameters().Length).Distinct().OrderBy(x => x).ToList();
            Assert.NotEmpty(gameOverloads);
            Assert.Equal(gameOverloads, patched);
        }

        [Fact]
        public void FuckScriptThief_targets_serialize_with_matching_names()
        {
            var rows = RowsFor("Serialize").Where(r => r.Shim.Name.Contains("FuckScriptThief")).ToList();
            Assert.NotEmpty(rows);
            foreach (var r in rows)
                Assert.Empty(PatchBindingValidator.Validate(r.Original, r.Patch, r.IsPrefix));
        }

        [Fact]
        public void Every_prefix_parameter_name_exists_on_target_or_is_special()
        {
            // belt & braces over the full row set, with a readable failure list
            var specials = new[] { "__instance", "__result", "__original", "__prefixSkipped" };
            var bad = new List<string>();
            foreach (var r in AllBindings.Rows)
            {
                foreach (var p in r.Patch.GetParameters())
                {
                    if (p.Name == null || specials.Contains(p.Name) || (p.Name.StartsWith("___")))
                        continue;
                    if (!r.Original.GetParameters().Any(tp => tp.Name == p.Name))
                        bad.Add($"{r}: '{p.Name}'");
                }
            }
            Assert.True(bad.Count == 0, "unbound parameters:\n" + string.Join("\n", bad));
        }
    }
}
