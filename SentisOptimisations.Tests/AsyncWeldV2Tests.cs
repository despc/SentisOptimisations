using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace SentisOptimisations.Tests;

/// <summary>
/// AsyncWeld v2 contract: the weld/grind pipeline must not reach game state from worker
/// threads. These tests pin the structural invariants of the redesign so a future
/// "optimization" cannot silently reintroduce the off-thread Havok/conveyor/grid races.
/// </summary>
public class AsyncWeldV2Tests
{
    private static Type PluginType(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(fullName))
            .FirstOrDefault(t => t != null);

    /// <summary>All methods of a type including compiler-generated closure/nested types.</summary>
    private static IEnumerable<Type> TypeWithNested(Type outer)
    {
        yield return outer;
        foreach (var n in outer.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        foreach (var x in TypeWithNested(n))
            yield return x;
    }

    /// <summary>
    /// Resolve every call/callvirt token in a method body (heuristic byte scan; token values
    /// that do not resolve are ignored, false positives against a specific member are negligible).
    /// </summary>
    private static IEnumerable<MethodBase> CalledMembers(MethodBase m)
    {
        byte[] il;
        try { il = m.GetMethodBody()?.GetILAsByteArray(); }
        catch { yield break; }
        if (il == null) yield break;
        var module = m.Module;
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F) continue; // call / callvirt
            int token = BitConverter.ToInt32(il, i + 1);
            MethodBase resolved = null;
            try { resolved = module.ResolveMethod(token); }
            catch { }
            if (resolved != null) yield return resolved;
        }
    }

    private static void AssertNoCallsTo(Type outer, Func<MethodBase, bool> forbidden, string why)
    {
        var offenders = new List<string>();
        foreach (var t in TypeWithNested(outer))
        foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                                       BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.DeclaredOnly))
        {
            foreach (var callee in CalledMembers(m))
            {
                if (forbidden(callee))
                    offenders.Add($"{t.Name}.{m.Name} -> {callee.DeclaringType?.Name}.{callee.Name}");
            }
        }

        Assert.True(offenders.Count == 0,
            why + Environment.NewLine + string.Join(Environment.NewLine, offenders.Distinct()));
    }

    [Fact]
    public void Weld_path_must_not_use_game_thread_marshaling_or_new_threads()
    {
        var shipToolPatch = PluginType("SentisOptimisationsPlugin.ShipTool.ShipToolPatch");
        var welderOpt = PluginType("Optimizer.Optimizations.WelderOptimization");
        Assert.NotNull(shipToolPatch);
        Assert.NotNull(welderOpt);

        bool Forbidden(MethodBase c)
        {
            var dn = c.DeclaringType?.FullName ?? "";
            if (dn.EndsWith("MyAPIGateway")) return true; // InvokeOnGameThread et al.
            if (dn == "System.Threading.Tasks.Task" && c.Name == "Run") return true;
            if (dn == "System.Threading.Thread" && (c.Name == "Start" || c.Name == ".ctor")) return true;
            return false;
        }

        // Everything in the weld/grind path runs on the game thread already; any
        // MyAPIGateway/Task.Run/Thread use there means work was moved off it again.
        AssertNoCallsTo(shipToolPatch, Forbidden,
            "ShipToolPatch must not marshal work to or spawn extra threads (AsyncWeld v2 contract)");
        AssertNoCallsTo(welderOpt, Forbidden,
            "WelderOptimization must not marshal work to or spawn extra threads (AsyncWeld v2 contract)");
    }

    [Fact]
    public void Il_scanner_actually_detects_TaskRun_negative_control()
    {
        // ShipToolsAsyncQueues.OnLoaded still does Task.Run(StartLoop); if the scanner cannot
        // see that, the guard tests above are vacuous.
        var queues = PluginType("SentisOptimisationsPlugin.ShipTool.ShipToolsAsyncQueues");
        Assert.NotNull(queues);
        var onLoaded = queues.GetMethod("OnLoaded", BindingFlags.Instance | BindingFlags.Public);
        var found = CalledMembers(onLoaded)
            .Any(c => c.DeclaringType?.FullName == "System.Threading.Tasks.Task" && c.Name == "Run");
        Assert.True(found, "IL call scanner failed to resolve Task.Run — guard tests are vacuous");
    }

    [Fact]
    public void Dead_offthread_pipeline_methods_stay_removed()
    {
        var shipToolPatch = PluginType("SentisOptimisationsPlugin.ShipTool.ShipToolPatch");
        Assert.NotNull(shipToolPatch);
        foreach (var name in new[] { "GetTopEntitiesInSphereAsync", "CollectTargetBlocksAsyncAndCallActivate" })
            Assert.Null(shipToolPatch.GetMethod(name,
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
    }

    [Fact]
    public void Weld_signature_has_no_async_callback()
    {
        var welderOpt = PluginType("Optimizer.Optimizations.WelderOptimization");
        Assert.NotNull(welderOpt);
        var weld = welderOpt.GetMethod("Weld", BindingFlags.Static | BindingFlags.Public);
        Assert.NotNull(weld);
        Assert.DoesNotContain(weld.GetParameters(), p => p.ParameterType.Name.Contains("Action"));
    }

    [Fact]
    public void Plugin_no_longer_instantiates_worker_weld_queues()
    {
        var plugin = PluginType("SentisOptimisationsPlugin.SentisOptimisationsPlugin");
        Assert.NotNull(plugin);
        Assert.DoesNotContain(plugin.GetFields(BindingFlags.Instance | BindingFlags.Public),
            f => f.FieldType.Name == "ShipToolsAsyncQueues");
    }

    [Fact]
    public void AsyncWeld_setting_is_removed()
    {
        var cfg = PluginHarness.Config; // real MainConfig instance
        Assert.NotNull(cfg);
        // The inert compatibility flag is gone; welding is always game-thread (AsyncWeld v2).
        // Old .cfg files may still contain <AsyncWeld>; deserialization ignores unknown members.
        Assert.Null(cfg.GetType().GetProperty("AsyncWeld"));
    }

    [Fact]
    public void Activation_is_the_games_own_after_the_throttle()
    {
        // The copy of ActivateCommon enumerated every grid of the world through
        // ConcurrentDictionary.Keys and allocated per activation; the prefix of
        // GetBlocksInsideSpheres copied the vanilla algorithm and let vanilla run after it anyway.
        var shipToolPatch = PluginType("SentisOptimisationsPlugin.ShipTool.ShipToolPatch");
        Assert.NotNull(shipToolPatch);
        foreach (var name in new[]
                 {
                     "DoActivateCommon", "GetTopMostEntitiesInSphereFast", "GetEntitiesInContact",
                     "ProcessEntitiesInContact", "CallActivate", "GetBlocksInsideSpheresPatch",
                 })
            Assert.Null(shipToolPatch.GetMethod(name,
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
    }
}
