using System.Reflection;
using Optimizer.Optimizations;
using VRage.Library.Compiler;
using Xunit;

namespace SentisOptimisations.Tests;

public class ScriptSaveCodeTests
{
    private class NoSave { }

    private class EmptySave
    {
        public void Save() { }
    }

    // what the game's compiler leaves in an empty method: its own counters
    private class CountedSave
    {
        private int _n;
        public void Save()
        {
            IlInjector.CountMethodCalls();
            IlInjector.CountInstructions();
            _n = _n + 1;
            if (_n > 3) IlInjector.CountInstructions();
        }
    }

    private class StoringSave
    {
        public string Storage { get; set; }
        public void Save() { Storage = "state"; }
    }

    private class HelperSave
    {
        private void Do() { }
        private void Save() { Do(); }
    }

    private class DelegateSave
    {
        public System.Action Kept;
        private void Do() { }
        public void Save() { Kept = Do; }
    }

    [Fact]
    public void A_script_without_Save_or_with_an_empty_one_has_no_code_there()
    {
        Assert.False(ScriptSaveCode.HasCode(typeof(NoSave)));
        Assert.False(ScriptSaveCode.HasCode(typeof(EmptySave)));
        Assert.False(ScriptSaveCode.HasCode(typeof(CountedSave)));
    }

    [Fact]
    public void Any_call_in_Save_is_code()
    {
        Assert.True(ScriptSaveCode.HasCode(typeof(StoringSave)));
        Assert.True(ScriptSaveCode.HasCode(typeof(HelperSave)));
        Assert.True(ScriptSaveCode.HasCode(typeof(DelegateSave)));
    }

    [Fact]
    public void A_method_without_a_body_counts_as_code()
    {
        Assert.True(ScriptSaveCode.HasCode((MethodInfo)null));
        Assert.True(ScriptSaveCode.HasCode(typeof(System.IDisposable).GetMethod("Dispose")));
    }
}
