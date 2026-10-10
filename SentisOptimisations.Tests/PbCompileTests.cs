using System;
using System.Linq;
using System.Reflection;
using SentisOptimisationsPlugin;
using Xunit;

namespace SentisOptimisations.Tests;

/// <summary>A script's assembly compiled to machine code ahead of its runs, and the game's members the off-thread compile uses.</summary>
public class PbCompileTests
{
    private class Sample
    {
        private int _n;
        public Sample() { _n = 1; }
        public int Add(int x) => _n + x;
        public T Echo<T>(T x) => x;                       // a generic definition: left for its first call
        private static string Join(string[] parts) => string.Join(",", parts.Select(p => p.Trim()));
        private System.Collections.Generic.IEnumerable<int> Many() { yield return _n; }
    }

    /// <summary>Whether <see cref="WithStaticConstructor"/>'s type initializer ran; a field of its own would run it on read.</summary>
    private static class StaticConstructorProbe
    {
        public static bool Ran;
    }

    /// <summary>
    /// A script's <c>static Settings() { Ini = new MyIni(); }</c>: the game wraps its body in the instruction counter,
    /// which throws outside a run - and a type initializer that threw throws for good.
    /// </summary>
    private static class WithStaticConstructor
    {
        public static int Value;
        static WithStaticConstructor() { StaticConstructorProbe.Ran = true; Value = 1; }
        public static int Get() => Value;
    }

    /// <summary>A script's <c>static Dictionary&lt;string, MyMinItem&gt; Components = { ["Motor"] = new MyMinItem(150) }</c>.</summary>
    private static class FieldInitializerMakesScriptObject
    {
        public static object Item = new Sample();
    }

    private static class FieldInitializerCallsScriptMethod
    {
        public static int Value = Make();
        private static int Make() => 1;
    }

    private static class FieldInitializerReadsAnotherScriptTypesStatics
    {
        // only read as IL by the tests: running it would run WithStaticConstructor's initializer
        public static int Value = WithStaticConstructor.Value;
    }

    /// <summary>A script's <c>static MyIni _ini = new MyIni();</c>: only the framework's and the game's objects.</summary>
    private static class FieldInitializerMakesGameObjects
    {
        public static VRage.Game.ModAPI.Ingame.Utilities.MyIni Ini = new VRage.Game.ModAPI.Ingame.Utilities.MyIni();
        public static System.Collections.Generic.List<int> List = new System.Collections.Generic.List<int> { 1, 2 };
        public static int[] Array = { 1, 2, 3, 4, 5, 6, 7, 8 };
        public static string Text = "text";
    }

    private static Type[] TypesOf(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); }
    }

    [Fact]
    public void Every_method_of_an_assembly_is_prepared_without_a_throw()
    {
        var count = PbCompile.PrepareTypes(TypesOf(typeof(Sample).Assembly));
        Assert.True(count > 50, count.ToString());
    }

    [Fact]
    public void A_type_with_a_static_constructor_is_left_for_its_first_call()
    {
        PbCompile.PrepareTypes(TypesOf(typeof(WithStaticConstructor).Assembly));
        Assert.False(StaticConstructorProbe.Ran, "Prepare ran a script's type initializer, off the game thread and outside a run");
        Assert.Equal(1, WithStaticConstructor.Get());   // the script's own first call runs it, as vanilla
        Assert.True(StaticConstructorProbe.Ran);
    }

    [Theory]
    [InlineData(typeof(FieldInitializerMakesScriptObject), true)]
    [InlineData(typeof(FieldInitializerCallsScriptMethod), true)]
    [InlineData(typeof(FieldInitializerReadsAnotherScriptTypesStatics), true)]
    [InlineData(typeof(FieldInitializerMakesGameObjects), false)]
    [InlineData(typeof(WithStaticConstructor), false)]   // a static constructor of its own: never run ahead by the JIT
    [InlineData(typeof(Sample), false)]                  // no static initialization at all
    public void A_field_initializer_with_the_scripts_code_is_found(Type type, bool runsScriptCode)
    {
        Assert.Equal(runsScriptCode, PbCompile.RunsScriptCodeAhead(type));
    }

    [Fact]
    public void An_assembly_whose_field_initializer_runs_its_code_is_left_whole()
    {
        Assert.Equal(0, PbCompile.Prepare(typeof(FieldInitializerMakesScriptObject).Assembly));
    }

    [Fact]
    public void The_game_still_has_what_the_off_thread_compile_uses()
    {
        PbCompile.Bind();
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var compile = typeof(Sandbox.Game.Entities.Blocks.MyProgrammableBlock).GetMethod("Compile", any);
        Assert.NotNull(compile);
        Assert.Equal(new[] { "program", "storage", "instantiate" }, compile.GetParameters().Select(p => p.Name));
    }
}
