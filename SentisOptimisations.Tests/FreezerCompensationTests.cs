using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SentisOptimisationsPlugin.Freezer;
using Xunit;

namespace SentisOptimisations.Tests
{
    /// <summary>
    /// Freeze / unfreeze / compensation mechanics. Behavioral tests for CompensationTracker
    /// (the bookkeeping that decides how many simulation frames a production block has to make
    /// up after unfreezing) plus structural game-thread-safety assertions on the patch methods.
    /// </summary>
    public class FreezerCompensationTests
    {
        // ---------------------------------------------------------------- tracker behavior

        [Fact]
        public void Unfreeze_accumulates_the_frozen_period()
        {
            const long block = 1001;
            CompensationTracker.OnFrozen(block, 1_000);
            Assert.True(CompensationTracker.OnUnfrozen(block, 7_000, 100_000));
            Assert.Equal(6_000u, CompensationTracker.PeekPending(block));
        }

        [Fact]
        public void Refreeze_before_apply_does_NOT_lose_the_first_period()
        {
            // The defect: unfreeze scheduled a deferred apply (+120 frames). If the grid froze
            // again before the apply landed, the second freeze stamp overwrote the first delta
            // and the whole first frozen period vanished.
            const long block = 1002;
            CompensationTracker.OnFrozen(block, 1_000);
            CompensationTracker.OnUnfrozen(block, 61_000, 10_000_000); // 60k frames pending
            CompensationTracker.OnFrozen(block, 61_100);               // refrozen, apply never ran
            CompensationTracker.OnUnfrozen(block, 121_100, 10_000_000); // +60k more

            Assert.Equal(120_000u, CompensationTracker.PeekPending(block));
        }

        [Fact]
        public void Compensation_is_taken_exactly_once()
        {
            const long block = 1003;
            CompensationTracker.OnFrozen(block, 0);
            CompensationTracker.OnUnfrozen(block, 500, 100_000);

            Assert.True(CompensationTracker.TryTakeCompensation(block, out var first));
            Assert.Equal(500u, first);
            Assert.False(CompensationTracker.TryTakeCompensation(block, out _),
                "a second apply applied the same compensation twice - double production");
        }

        [Fact]
        public void Pending_survives_a_scheduled_apply_skipped_by_refreeze()
        {
            const long block = 1004;
            CompensationTracker.OnFrozen(block, 0);
            CompensationTracker.OnUnfrozen(block, 900, 100_000);
            Assert.True(CompensationTracker.TryScheduleApply(block));
            Assert.False(CompensationTracker.TryScheduleApply(block),
                "double scheduling must not produce two applies for one accumulation");

            // the deferred apply fires, sees the block frozen again, releases the schedule
            CompensationTracker.OnFrozen(block, 901);
            CompensationTracker.ReleaseSchedule(block);
            Assert.True(CompensationTracker.IsFrozen(block));

            // unfreeze later: the total is still there for the apply of the NEXT cycle
            CompensationTracker.OnUnfrozen(block, 1_400, 100_000);
            Assert.True(CompensationTracker.TryTakeCompensation(block, out var frames));
            Assert.Equal(900u + 499u, frames); // first period + second period, nothing lost
        }

        [Fact]
        public void Future_stamp_from_recycled_entity_id_is_dropped_not_wrapped()
        {
            // EntityIds are reused after deletion. A stamp from a dead block lies in the future
            // for its successor; the old (uint)(counter - stamp) wrapped to ~4e9 frames and the
            // refinery consumed its whole queue in one tick.
            const long block = 1005;
            CompensationTracker.OnFrozen(block, 500_000); // stale "future" stamp
            Assert.False(CompensationTracker.OnUnfrozen(block, 100, 100_000),
                "future stamp produced a compensation - wrapped-around delta");
            Assert.Null(CompensationTracker.PeekPending(block));

            // the successor of the recycled id now behaves normally
            CompensationTracker.OnFrozen(block, 100);
            Assert.True(CompensationTracker.OnUnfrozen(block, 200, 100_000));
            Assert.Equal(100u, CompensationTracker.PeekPending(block));
        }

        [Fact]
        public void Absurd_delta_is_clamped_to_the_sanity_cap()
        {
            const long block = 1006;
            CompensationTracker.OnFrozen(block, 10);
            CompensationTracker.OnUnfrozen(block, 10 + 10_000_000_000, maxFrames: 4096);
            Assert.Equal(4096u, CompensationTracker.PeekPending(block));
        }

        [Fact]
        public void Forget_drops_all_state_for_a_block()
        {
            const long block = 1007;
            CompensationTracker.OnFrozen(block, 0);
            CompensationTracker.OnUnfrozen(block, 500, 100_000);
            CompensationTracker.TryScheduleApply(block);

            CompensationTracker.Forget(block);

            Assert.False(CompensationTracker.IsFrozen(block));
            Assert.Null(CompensationTracker.PeekPending(block));
            Assert.False(CompensationTracker.TryTakeCompensation(block, out _));
            Assert.True(CompensationTracker.TryScheduleApply(block),
                "Forget left the schedule marker stuck - the block could never compensate again");
        }

        [Fact]
        public void Concurrent_blocks_compensate_without_loss()
        {
            const int blocks = 64;
            var ids = Enumerable.Range(5_000, blocks).ToArray();
            Parallel.For(0, blocks, i =>
            {
                var id = ids[i];
                CompensationTracker.OnFrozen(id, (ulong)i);
                CompensationTracker.OnUnfrozen(id, (ulong)(i + 1000), 1_000_000);
                CompensationTracker.OnFrozen(id, (ulong)(i + 1000));
                CompensationTracker.OnUnfrozen(id, (ulong)(i + 2000), 1_000_000);
            });
            for (int i = 0; i < blocks; i++)
            {
                Assert.True(CompensationTracker.TryTakeCompensation(ids[i], out var frames));
                Assert.Equal(2000u, frames);
            }
        }

        // ------------------------------------------------------------- compensation-pass gate

        [Fact]
        public void Overflow_handling_is_active_only_inside_a_big_catch_up_pass()
        {
            Assert.False(CompensationTracker.InCompensation());

            CompensationTracker.RunCompensationPass(36_000u, () =>
                Assert.True(CompensationTracker.InCompensation(),
                    "the AddItems overflow dance must be active during a compensation burst"));

            CompensationTracker.RunCompensationPass(5u, () =>
                Assert.False(CompensationTracker.InCompensation(),
                    "a normal one-frame tick is NOT a compensation - vanilla AddItems must run"));

            Assert.False(CompensationTracker.InCompensation());

            // even when the pass throws, the gate must not stay on
            Assert.Throws<InvalidOperationException>(() =>
                CompensationTracker.RunCompensationPass(36_000u, () => throw new InvalidOperationException()));
            Assert.False(CompensationTracker.InCompensation());
        }

        // ------------------------------------------- structural game-thread / cleanup checks

        static readonly Type Patches = typeof(FreezerPatches);

        static bool Calls(MethodBase method, string targetName, string declaringFullName = null)
        {
            var body = method.GetMethodBody()?.GetILAsByteArray();
            if (body == null) return false;
            var module = method.Module;
            for (int i = 0; i < body.Length - 4; i++)
            {
                // call (0x28) / callvirt (0x6F)
                if (body[i] != 0x28 && body[i] != 0x6F) continue;
                int token = BitConverter.ToInt32(body, i + 1);
                MethodBase called;
                try { called = module.ResolveMethod(token); }
                catch { continue; }
                if (called.Name != targetName) continue;
                if (declaringFullName != null && called.DeclaringType?.FullName != declaringFullName)
                    continue;
                return true;
            }
            return false;
        }

        static MethodInfo PatchMethod(string name) =>
            Patches.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        [Theory]
        [InlineData("FinishAssembling")]
        [InlineData("FinishDisassembling")]
        [InlineData("AsyncUpdateAssemblerProduction")]
        [InlineData("AsyncCollectAssemblerRequiredItems")]
        public void Compensation_mutation_runs_inline_not_deferred(string methodName)
        {
            // The defect: the decision (inventory snapshot) ran on a background thread while the
            // mutation (RemoveItemsOfType/AddItems/queue edits) was posted with
            // InvokeOnGameThread. Whatever the conveyor moved between the two was lost or
            // duplicated. Everything must now execute inline on the game thread in one pass.
            var m = PatchMethod(methodName);
            Assert.NotNull(m);
            Assert.False(Calls(m, "InvokeOnGameThread"),
                $"{methodName} still defers part of the compensation to a later game-thread pass");
            Assert.False(Calls(m, "Sleep", "System.Threading.Thread"),
                $"{methodName} still sleeps - it blocks the shared delayed-action loop");
        }

        [Fact]
        public void Assembler_and_refinery_compensation_run_on_the_game_thread()
        {
            // The prefixes must schedule the fast-forward via InvokeOnGameThread (game thread),
            // not via DelayedProcessor (background thread).
            foreach (var name in new[] { "UpdateProductionAssembler", "GetComponentsFromConveyorPatch" })
            {
                var m = PatchMethod(name);
                Assert.NotNull(m);
                Assert.True(Calls(m, "InvokeOnGameThread"),
                    $"{name} does not dispatch to the game thread");
                Assert.False(Calls(m, "AddDelayedAction"),
                    $"{name} still runs production logic on the delayed/background thread");
            }
        }

        [Theory]
        [InlineData("UpdateProductionAssembler")]
        [InlineData("UpdateProductionRefinery")]
        public void Production_prefixes_skip_frozen_blocks(string methodName)
        {
            // A block whose unfreeze apply is in flight used to also run vanilla production in
            // the same frames - double counting. The prefix must consult the frozen state.
            var m = PatchMethod(methodName);
            Assert.NotNull(m);
            Assert.True(Calls(m, "IsFrozen", typeof(CompensationTracker).FullName),
                $"{methodName} produces for blocks that are still frozen");
        }

        [Fact]
        public void AddItems_overflow_handling_is_gated_to_compensation()
        {
            var m = PatchMethod("AddItemsPatched");
            Assert.NotNull(m);
            Assert.True(Calls(m, "InCompensation", typeof(CompensationTracker).FullName),
                "AddItemsPatched still rewrites every vanilla production-inventory AddItems call");
        }

        [Fact]
        public void Entity_removal_drops_frozen_and_compensation_state()
        {
            var observer = typeof(SentisOptimisationsPlugin.Freezer.FreezeLogic)
                .Assembly.GetType("SentisGameplayImprovements.AllGridsActions.EntitiesObserver");
            Assert.NotNull(observer);
            var m = observer.GetMethod("MyEntitiesOnOnEntityRemove", BindingFlags.Static | BindingFlags.Public);
            Assert.NotNull(m);
            Assert.True(Calls(m, "ForgetGrid", typeof(FreezeLogic).FullName),
                "grid removal must clear frozen sets, wake-up schedule AND compensation stamps");
        }

        [Theory]
        [InlineData("FrozenGrids")]
        [InlineData("FrozenPhysicsGrids")]
        [InlineData("InFreezeQueue")]
        public void Freeze_state_sets_are_thread_safe(string fieldName)
        {
            // the FreezerLoop (background) and Harmony prefixes/game thread hammer these sets
            var f = typeof(FreezeLogic).GetField(fieldName, BindingFlags.Static | BindingFlags.Public);
            Assert.NotNull(f);
            Assert.False(f.FieldType.IsGenericType &&
                         f.FieldType.GetGenericTypeDefinition().Name.StartsWith("HashSet"),
                $"{fieldName} is a plain HashSet mutated from multiple threads");
        }

        [Fact]
        public void Concurrent_freeze_state_survives_multi_threaded_churn()
        {
            var set = new SentisOptimisations.Utils.ConcurrentHashSet<long>();
            var errors = new List<Exception>();
            var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 2000; i++)
                    {
                        long id = (t * 2000) + i;
                        set.Add(id);
                        set.Contains(id);
                        if (i % 2 == 0) set.Remove(id);
                    }
                }
                catch (Exception e) { lock (errors) errors.Add(e); }
            })).ToList();
            threads.ForEach(x => x.Start());
            threads.ForEach(x => x.Join());
            Assert.Empty(errors);
        }
    }
}
