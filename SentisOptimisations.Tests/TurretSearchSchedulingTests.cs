using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using Sandbox.Game.Weapons;
using SpaceEngineers.Game.Entities.Blocks;
using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class TurretSearchSchedulingTests
    {
        private static bool Due(ulong frame, long id, bool target) => (bool)typeof(TurretControlTargetSelect)
            .GetMethod("Due", BindingFlags.Static | BindingFlags.NonPublic, null,
                new[] { typeof(ulong), typeof(long), typeof(bool) }, null)?.Invoke(null, new object[] { frame, id, target });

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Real_game_IL_changes_only_the_two_search_calls_preserving_all_vanilla_checks(bool normal)
        {
            var method = normal ? typeof(MyLargeTurretBase).GetMethod("DoUpdateTimerTick") : typeof(MyTurretControlBlock).GetMethod("UpdateAfterSimulation");
            var transpiler = typeof(TurretControlTargetSelect).GetMethod(normal ? "VanillaTranspiler" : "Transpiler", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(transpiler);
            var original = PatchUtilities.ReadInstructions(method).ToList();
            var output = ((IEnumerable<MsilInstruction>)transpiler.Invoke(null, new object[] { original })).ToList();
            var search = typeof(MyLargeTurretTargetingSystem).GetMethod("CheckAndSelectNearTargetsParallel");
            var wrapper = typeof(TurretControlTargetSelect).GetMethod(normal ? "SearchVanillaDue" : "SearchDue");
            Assert.NotNull(wrapper);
            var src = 0;
            var replacements = 0;
            for (var dst = 0; dst < output.Count; dst++, src++)
            {
                if (original[src].Operand is MsilOperandInline<MethodBase> operand && operand.Value == search)
                {
                    Assert.Equal(OpCodes.Ldarg_0, output[dst].OpCode);
                    Assert.Equal(original[src].Labels, output[dst].Labels);
                    dst++;
                    Assert.Equal(OpCodes.Call, output[dst].OpCode);
                    Assert.Equal(wrapper, ((MsilOperandInline<MethodBase>)output[dst].Operand).Value);
                    replacements++;
                }
                else
                {
                    Assert.Same(original[src], output[dst]);
                }
            }
            Assert.Equal(original.Count, src);
            Assert.Equal(2, replacements);
        }

        [Fact]
        public void Vanilla_scheduler_is_pumped_from_plugin_Update_and_rechecks_vanilla_eligibility()
        {
            var tick = typeof(TurretControlTargetSelect).GetMethod("Tick");
            Assert.NotNull(tick);
            var calls = PatchUtilities.ReadInstructions(typeof(SentisOptimisationsPlugin.SentisOptimisationsPlugin).GetMethod("Update"));
            Assert.Contains(calls, i => i.Operand is MsilOperandInline<MethodBase> op && op.Value == tick);
            var method = typeof(TurretControlTargetSelect).GetMethod("SearchIdleVanilla", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var names = PatchUtilities.ReadInstructions(method).Where(i => i.Operand is MsilOperandInline<MethodBase>)
                .Select(i => ((MsilOperandInline<MethodBase>)i.Operand).Value.Name).ToArray();
            foreach (var name in new[] { "get_Target", "get_IsWorking", "get_Enabled", "get_IsControlled", "get_IsTargetPositionManual", "IsVisible", "get_IsUpdateTimerEnabled", "Invoke", "CheckAndSelectNearTargetsParallel" })
                Assert.Contains(name, names);
        }

        [Fact]
        public void Optional_live_probe_observes_forwarded_searches_and_cannot_break_gameplay()
        {
            var type = typeof(TurretControlTargetSelect);
            var field = type.GetField("SearchObserved");
            var observe = type.GetMethod("Observe", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(field);
            Assert.NotNull(observe);
            var seen = new List<Tuple<long, ulong, bool, bool>>();
            try
            {
                Action<long, ulong, bool, bool> probe = (id, frame, target, normal) => seen.Add(Tuple.Create(id, frame, target, normal));
                field.SetValue(null, probe);
                observe.Invoke(null, new object[] { 71L, 42UL, true, false });
                Assert.Equal(Tuple.Create(71L, 42UL, true, false), Assert.Single(seen));
                field.SetValue(null, (Action<long, ulong, bool, bool>)((a, b, c, d) => { throw new Exception("probe failure"); }));
                observe.Invoke(null, new object[] { 71L, 43UL, false, true });
                foreach (var name in new[] { "SearchDue", "SearchVanillaDue", "SearchIdleVanilla" })
                {
                    var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    Assert.Contains(PatchUtilities.ReadInstructions(method), i => i.Operand is MsilOperandInline<MethodBase> op && op.Value == observe);
                }
            }
            finally { field.SetValue(null, null); }
        }

        private sealed class TestTurret
        {
            public long Id;
            public bool HasTarget;
            public readonly System.Collections.Generic.List<ulong> Searches = new System.Collections.Generic.List<ulong>();
        }

        [Fact]
        public void Vanilla_ten_frame_timer_cannot_starve_any_of_twenty_block_phases()
        {
            var type = typeof(TurretControlTargetSelect).Assembly.GetType("Optimizer.Optimizations.TurretSearchScheduler`1");
            Assert.NotNull(type);
            type = type.MakeGenericType(typeof(TestTurret));
            var scheduler = Activator.CreateInstance(type);
            var register = type.GetMethod("Register");
            var tick = type.GetMethod("Tick");
            var turrets = Enumerable.Range(0, 20).Select(id => new TestTurret { Id = id }).ToArray();
            ulong now = 0;
            Action<TestTurret> search = block => { if (!block.HasTarget) block.Searches.Add(now); };
            for (now = 0; now < 100; now++)
            {
                if (now % 10 == 0)
                    foreach (var block in turrets) register.Invoke(scheduler, new object[] { block, block.Id });
                tick.Invoke(scheduler, new object[] { now, search });
                tick.Invoke(scheduler, new object[] { now, search }); // paused/repeated plugin update must not duplicate
            }
            foreach (var block in turrets)
            {
                Assert.Equal(5, block.Searches.Count);
                Assert.All(block.Searches, f => Assert.Equal((ulong)block.Id, f % 20));
                Assert.All(block.Searches.Zip(block.Searches.Skip(1), (a, b) => b - a), gap => Assert.Equal(20UL, gap));
            }
            Assert.Equal(20, turrets.Select(t => t.Searches[0]).Distinct().Count());
            type.GetMethod("Clear").Invoke(scheduler, null);
            tick.Invoke(scheduler, new object[] { 107UL, search });
            Assert.All(turrets, t => Assert.Equal(5, t.Searches.Count));
        }

        [Fact]
        public void Target_acquisition_uses_ten_frame_phase_and_loss_restores_twenty()
        {
            // Target != null includes potential targets, but aiming/shooting are not gated by Due.
            for (long id = 0; id < 40; id++)
            {
                var active = Enumerable.Range(0, 100).Where(f => Due((ulong)f, id, true)).ToArray();
                Assert.Equal(10, active.Length);
                Assert.All(active.Zip(active.Skip(1), (a, b) => b - a), gap => Assert.Equal(10, gap));
            }
            Assert.False(Due(16, 7, true));
            Assert.True(Due(17, 7, true));
            Assert.False(Due(17, 7, false));
            Assert.True(Due(27, 7, false));
            Assert.False(Due(28, 7, false));
        }

        [Fact]
        public void Idle_controller_searches_once_per_twenty_frames_per_block()
        {
            Assert.Equal(20, TurretControlTargetSelect.Period);
            for (long id = 0; id < 40; id++)
            {
                var frames = Enumerable.Range(0, 100).Where(f => Due((ulong)f, id, false)).ToArray();
                Assert.Equal(5, frames.Length);
                Assert.All(frames.Zip(frames.Skip(1), (a, b) => b - a), gap => Assert.Equal(20, gap));
            }
            Assert.Equal(20, Enumerable.Range(0, 20).Select(id => Enumerable.Range(0, 20).Single(f => Due((ulong)f, id, false))).Distinct().Count());
        }
    }
}
