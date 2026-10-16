using System;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using SentisOptimisationsPlugin;
using VRage.Game.ModAPI;
using Xunit;

namespace SentisOptimisations.Tests;

public class PhysicsGuardTests
{
    [Fact]
    public void Waits_the_first_minute_after_the_world_is_loaded()
    {
        var loaded = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(PhysicsGuard.Waiting(loaded, loaded));
        Assert.True(PhysicsGuard.Waiting(loaded.AddSeconds(59), loaded));
        Assert.False(PhysicsGuard.Waiting(loaded.AddSeconds(60), loaded));
        Assert.False(PhysicsGuard.Waiting(loaded.AddMinutes(30), loaded));
    }

    [Fact]
    public void Does_not_wait_when_no_load_was_noted()
    {
        Assert.False(PhysicsGuard.Waiting(DateTime.UtcNow, null));
    }

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void The_measured_physics_update_exists_and_takes_no_arguments()
    {
        var simulate = typeof(MyPhysics).GetMethod(nameof(MyPhysics.Simulate),
            BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
        Assert.NotNull(simulate);
        Assert.Equal(typeof(void), simulate.ReturnType);
    }

    [Fact]
    public void The_members_the_guard_reads_exist()
    {
        // The clusters to walk, the bodies that cost the step, and the way back to the entity.
        Assert.NotNull(typeof(MyPhysics).GetField(nameof(MyPhysics.Clusters), BindingFlags.Static | BindingFlags.Public));
        var active = typeof(HkWorld).GetProperty(nameof(HkWorld.ActiveRigidBodies));
        Assert.NotNull(active);
        Assert.NotNull(typeof(HkRigidBody).GetProperty("UserObject", Any) ?? (MemberInfo)typeof(HkRigidBody).GetField("UserObject", Any));
        Assert.NotNull(typeof(MyPhysicsBody).GetProperty(nameof(MyPhysicsBody.Entity), Any));
    }

    [Fact]
    public void The_share_SentisWatcher_reads_has_its_shape()
    {
        // SentisWatcher draws the grids' physics by this very method, through a Func<double, Dictionary<long, double>, int>.
        var share = typeof(Optimizer.Optimizations.PhysicsCost).GetMethod("Share", new[] { typeof(double), typeof(System.Collections.Generic.Dictionary<long, double>) });
        Assert.NotNull(share);
        Assert.True(share.IsStatic && share.IsPublic);
        Assert.Equal(typeof(int), share.ReturnType);
    }

    [Fact]
    public void The_members_the_clusters_are_timed_with_exist()
    {
        Assert.NotNull(typeof(MyPhysics).GetMethod("StepWorldsParallel", Any, null, Type.EmptyTypes, null));
        Assert.NotNull(typeof(MyPhysics).GetMethod("IsClusterActive", Any, null, new[] { typeof(int), typeof(int) }, null));
        Assert.Equal(typeof(HkJobQueue), typeof(MyPhysics).GetField("m_jobQueue", Any | BindingFlags.Static)?.FieldType);
        Assert.Equal(typeof(HkJobThreadPool), typeof(MyPhysics).GetField("m_threadPool", Any | BindingFlags.Static)?.FieldType);
    }

    [Fact]
    public void The_guard_reports_at_most_a_handful_of_groups_per_check()
    {
        var max = typeof(PhysicsGuard).GetField("MaxGroupsPerCheck", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(max);
        Assert.InRange((int)max.GetRawConstantValue(), 1, 10);
    }

    [Fact]
    public void Punish_counters_expire()
    {
        var lifetime = typeof(Punisher).GetField("CounterLifetime", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(lifetime);
        Assert.True((TimeSpan)lifetime.GetValue(null) > TimeSpan.Zero);
    }

    [Fact]
    public void Punish_entry_points_take_one_group_of_grids()
    {
        foreach (var name in new[] { "AlertPlayerGrid", "PunishPlayerGrid", "PunishPlayerGridImmediately" })
        {
            var method = typeof(Punisher).GetMethod(name, Any);
            Assert.NotNull(method);
            Assert.Equal(typeof(System.Collections.Generic.List<IMyCubeGrid>), method.GetParameters().Single().ParameterType);
        }
    }

    [Fact]
    public void The_mechanical_group_lookup_exists()
    {
        var groups = typeof(MyCubeGridGroups).GetMethod(nameof(MyCubeGridGroups.GetGroups), Any);
        Assert.NotNull(groups);
        Assert.Equal(typeof(GridLinkTypeEnum), groups.GetParameters().Single().ParameterType);
    }
}
