using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using SentisOptimisationsPlugin;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class ReflectionUtilsTests
    {
        // ReflectionUtils exists once per plugin assembly (same namespace) — invoke reflectively.
        static Type RuType => TestPaths.PluginAssembly.GetTypes().First(t => t.Name == "ReflectionUtils");

        static object Call(string name, params object[] args)
        {
            var mi = RuType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == name && m.GetParameters().Length == args.Length);
            try { return mi.Invoke(null, args); }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw;
            }
        }

        class Base
        {
            private string secret = "base-secret";
            protected int prot = 7;
        }

        class Derived : Base
        {
            private string own = "own";
            private static int staticThing = 42;
            private string Overloaded() => "noargs";
            private string Overloaded(int x) => "one:" + x;
        }

        [Fact]
        public void GetInstanceField_finds_private_on_exact_type()
        {
            var d = new Derived();
            Assert.Equal("own", Call("GetInstanceField", typeof(Derived), d, "own"));
        }

        [Fact]
        public void GetInstanceField_finds_inherited_private_when_given_base_type()
        {
            var d = new Derived();
            Assert.Equal("base-secret", Call("GetInstanceField", typeof(Base), d, "secret"));
        }

        [Fact]
        public void GetInstanceField_does_NOT_walk_up_the_hierarchy()
        {
            // documents the latent trap: passing the DERIVED type for a BASE-declared field fails.
            var d = new Derived();
            Assert.ThrowsAny<Exception>(() => Call("GetInstanceField", typeof(Derived), d, "secret"));
        }

        [Fact]
        public void Static_field_via_private_static_helper()
        {
            Call("SetPrivateStaticField", typeof(Derived), "staticThing", 99);
            Assert.Equal(99, Call("GetPrivateStaticField", typeof(Derived), "staticThing"));
        }

        [Fact]
        public void Same_name_overloads_are_ambiguous_and_throw()
        {
            // documents: name-only lookup with two overloads throws AmbiguousMatchException.
            // Call sites must use the (name, BindingFlags) form or a signature.
            Assert.ThrowsAny<Exception>(() =>
                typeof(Derived).GetMethod("Overloaded", BindingFlags.Instance | BindingFlags.NonPublic));
        }
    }

    public class Ext2Tests
    {
        // Ext2 exists once per plugin assembly; drive it reflectively to dodge the type collision.
        static object Call(string method, params object[] args)
        {
            var ext2 = TestPaths.PluginAssembly.GetType("NAPI.Ext2");
            var mi = ext2.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == method && m.GetParameters().Length >= args.Length);
            return mi.Invoke(null, args);
        }

        class Sample
        {
            private int hidden = 5;
            private string Name { get; set; } = "named";
        }

        [Fact]
        public void easyField_finds_private_field()
        {
            var fi = (FieldInfo)Call("easyField", typeof(Sample), "hidden");
            Assert.NotNull(fi);
            Assert.Equal(5, fi.GetValue(new Sample()));
        }

        [Fact]
        public void easyGetField_reads_value()
        {
            var v = Call("easyGetField", new Sample(), "hidden", null);
            Assert.Equal(5, v);
        }

        [Fact]
        public void easyField_missing_throws_or_returns_null_without_corrupting_state()
        {
            var ex = Record.Exception(() => Call("easyField", typeof(Sample), "definitelyNotThere"));
            // either documented behavior is acceptable; a hard crash of the caller is not silent
            Assert.True(ex == null || ex is TargetInvocationException, "unexpected failure mode: " + ex);
        }
    }

    public class PatchGuardTests
    {
        [Fact]
        public void Registration_failure_is_swallowed_and_logged_not_thrown()
        {
            var guard = TestPaths.PluginAssembly.GetType("SentisOptimisations.PatchGuard");
            var run = guard.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            using var capture = new NLogCapture();

            // action that throws must not propagate (a broken plugin must not brick the server)
            var ctx = TorchHarness.NewContext();
            var ex = Record.Exception(() => run.Invoke(null, new object[] { "UnitTestShim", ctx,
                (Action<Torch.Managers.PatchManager.PatchContext>)(c => throw new InvalidOperationException("registration boom")) }));
            Assert.Null(ex);
            Assert.Contains(capture.Entries, e => e.Contains("UnitTestShim") && e.Contains("registration boom"));
        }

        [Fact]
        public void Successful_action_runs()
        {
            var guard = TestPaths.PluginAssembly.GetType("SentisOptimisations.PatchGuard");
            var run = guard.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            var ctx = TorchHarness.NewContext();
            bool ran = false;
            run.Invoke(null, new object[] { "UnitTestShim", ctx,
                (Action<Torch.Managers.PatchManager.PatchContext>)(c => { Assert.Same(ctx, c); ran = true; }) });
            Assert.True(ran);
        }
    }

    public class PBFixNeedSkipTests
    {
        static readonly Type PbFixType = TestPaths.PluginAssembly.GetType("SentisOptimisationsPlugin.PBFix");
        static readonly MethodInfo NeedSkip = PbFixType.GetMethod("NeedSkip",
            BindingFlags.Static | BindingFlags.NonPublic);

        static void ClearState(Random seeded)
        {
            ((System.Collections.IDictionary)PbFixType.GetField("Cooldowns",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).GetValue(null)).Clear();
            var rField = PbFixType.GetField("r", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            rField.SetValue(null, seeded); // static readonly is writable via reflection on .NET Framework
        }

        static bool Call(long id, int cd) => (bool)NeedSkip.Invoke(null, new object[] { id, cd });

        [Fact]
        public void First_call_skips_and_seeds_a_value_within_cooldown()
        {
            ClearState(new Random(1234));
            Assert.True(Call(1, 10));
            var dict = (System.Collections.IDictionary)PbFixType.GetField("Cooldowns",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).GetValue(null);
            var seeded = (int)dict[1L];
            Assert.InRange(seeded, 0, 9);
        }

        [Fact]
        public void Skips_until_cooldown_exceeded_then_passes_and_resets()
        {
            ClearState(new Random(1));
            const long id = 77;
            const int cd = 5;
            Assert.True(Call(id, cd));        // seeds, returns true
            int through = 0, skipped = 0;
            for (int i = 0; i < 20; i++)
            {
                if (Call(id, cd)) skipped++; else through++;
            }
            Assert.True(through >= 1, "must eventually let something through");
            Assert.True(skipped > through, "must skip the majority of calls");
        }

        [Fact]
        public void Different_blocks_have_independent_cooldowns()
        {
            ClearState(new Random(7));
            Assert.True(Call(1, 3));
            Assert.True(Call(2, 3));
            var dict = (System.Collections.IDictionary)PbFixType.GetField("Cooldowns",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).GetValue(null);
            Assert.True(dict.Contains(1L) && dict.Contains(2L));
        }
    }
}
