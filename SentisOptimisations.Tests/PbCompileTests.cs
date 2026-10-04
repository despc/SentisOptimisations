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

    [Fact]
    public void Every_method_of_an_assembly_is_prepared_without_a_throw()
    {
        var count = PbCompile.Prepare(typeof(Sample).Assembly);
        Assert.True(count > 50, count.ToString());
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
