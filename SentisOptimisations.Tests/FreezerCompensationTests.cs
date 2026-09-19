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
        // ---------------------------------------------------------------- physics freeze

        [Fact]
        public void Frozen_bodies_can_leave_the_active_set()
        {
            // Without it every physics-frozen grid is walked by UpdateActiveRigidBodies each frame.
            var field = typeof(FreezeLogic).GetField("RigidBodyDeactivated", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(field);
            Assert.NotNull(field.GetValue(null));
        }

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

        [Fact]
        public void ClearAll_removes_every_tracker_dictionary_across_world_reload()
        {
            const long id = 782;
            CompensationTracker.OnFrozen(id, 100);
            CompensationTracker.OnUnfrozen(id, 200, 28800);
            CompensationTracker.TryTakeCompensation(id, out var frames);
            CompensationTracker.RunTrackedCompensationPass(id, frames, () => { });
            CompensationTracker.ClearAll();
            Assert.False(CompensationTracker.IsFrozen(id));
            Assert.Null(CompensationTracker.PeekPending(id));
            Assert.Null(CompensationTracker.PeekTakenFrames(id));
            Assert.Null(CompensationTracker.PeekAppliedFrames(id));
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
        public void Failed_custom_assembler_pass_never_falls_through_to_vanilla()
        {
            var fallback = PatchMethod("CustomAssemblerPassFailureFallback");
            Assert.NotNull(fallback);
            Assert.False((bool)fallback.Invoke(null, null),
                "vanilla must not run after a possibly partial custom mutation");
        }

        [Fact]
        public void Failed_custom_refinery_pass_never_falls_through_to_vanilla()
        {
            var fallback = PatchMethod("CustomRefineryPassFailureFallback");
            Assert.NotNull(fallback);
            Assert.False((bool)fallback.Invoke(null, null),
                "vanilla must not run after a possibly partial refinery mutation");
        }

        [Fact]
        public void Successful_compensation_pass_is_recorded_but_failed_pass_is_not()
        {
            const long block = 12001;
            CompensationTracker.Forget(block);
            CompensationTracker.OnFrozen(block, 100);
            Assert.True(CompensationTracker.OnUnfrozen(block, 700, 28800));
            Assert.True(CompensationTracker.TryTakeCompensation(block, out var frames));
            Assert.Throws<InvalidOperationException>(() =>
                CompensationTracker.RunTrackedCompensationPass(block, frames,
                    () => throw new InvalidOperationException("expected")));
            Assert.Null(CompensationTracker.PeekAppliedFrames(block));

            CompensationTracker.RunTrackedCompensationPass(block, frames, () => { });
            Assert.Equal(600UL, CompensationTracker.PeekAppliedFrames(block));
            CompensationTracker.Forget(block);
            Assert.Null(CompensationTracker.PeekAppliedFrames(block));
        }

        [Fact]
        public void Taken_and_applied_ledgers_match_after_exactly_once_pass()
        {
            const long id = 778;
            CompensationTracker.Forget(id);
            CompensationTracker.OnFrozen(id, 100);
            Assert.True(CompensationTracker.OnUnfrozen(id, 5100, 28800));
            Assert.True(CompensationTracker.TryTakeCompensation(id, out var frames));
            CompensationTracker.RunTrackedCompensationPass(id, frames, () => { });

            Assert.Equal((ulong?)5000, CompensationTracker.PeekTakenFrames(id));
            Assert.Equal(CompensationTracker.PeekTakenFrames(id),
                CompensationTracker.PeekAppliedFrames(id));
            CompensationTracker.Forget(id);
        }

        [Fact]
        public void Applied_ledger_records_only_injected_frozen_frames_not_vanilla_timer_frames()
        {
            const long id = 780;
            CompensationTracker.Forget(id);
            CompensationTracker.OnFrozen(id, 100);
            Assert.True(CompensationTracker.OnUnfrozen(id, 5100, 28800));
            Assert.True(CompensationTracker.TryTakeCompensation(id, out var frozenFrames));

            CompensationTracker.RunTrackedCompensationPass(id, frozenFrames + 120, () => { });

            Assert.Equal((ulong?)5000, CompensationTracker.PeekTakenFrames(id));
            Assert.Equal(CompensationTracker.PeekTakenFrames(id),
                CompensationTracker.PeekAppliedFrames(id));
            CompensationTracker.Forget(id);
        }

        [Fact]
        public void Short_valid_compensation_is_recorded_after_successful_pass()
        {
            const long id = 781;
            CompensationTracker.Forget(id);
            CompensationTracker.OnFrozen(id, 100);
            Assert.True(CompensationTracker.OnUnfrozen(id, 130, 28800));
            Assert.True(CompensationTracker.TryTakeCompensation(id, out var frames));
            CompensationTracker.CompleteProductionPass(id, frames);
            Assert.Equal((ulong?)30, CompensationTracker.PeekTakenFrames(id));
            Assert.Equal(CompensationTracker.PeekTakenFrames(id),
                CompensationTracker.PeekAppliedFrames(id));
            CompensationTracker.Forget(id);
        }

        [Fact]
        public void Cancelling_inactive_compensation_preserves_history_until_entity_removal()
        {
            const long id = 779;
            CompensationTracker.Forget(id);
            CompensationTracker.OnFrozen(id, 100);
            Assert.True(CompensationTracker.OnUnfrozen(id, 4100, 28800));
            Assert.True(CompensationTracker.TryTakeCompensation(id, out var frames));
            CompensationTracker.RunTrackedCompensationPass(id, frames, () => { });

            CompensationTracker.CancelPending(id);
            Assert.Equal((ulong?)4000, CompensationTracker.PeekTakenFrames(id));
            Assert.Equal((ulong?)4000, CompensationTracker.PeekAppliedFrames(id));
            CompensationTracker.Forget(id);
            Assert.Null(CompensationTracker.PeekTakenFrames(id));
            Assert.Null(CompensationTracker.PeekAppliedFrames(id));
        }

        [Fact]
        public void Assembler_update_does_not_queue_work_past_a_freeze_boundary()
        {
            // UpdateProduction is already a game-thread callback. Re-posting its work allows an
            // update observed before freeze to mutate inventories after the block is frozen.
            var assembler = PatchMethod("UpdateProductionAssembler");
            Assert.NotNull(assembler);
            Assert.False(Calls(assembler, "InvokeOnGameThread"),
                "assembler update defers work that can execute after the grid freezes");
            Assert.False(Calls(assembler, "AddDelayedAction"));
            Assert.True(Calls(assembler, "RunTrackedCompensationPass", typeof(CompensationTracker).FullName),
                "assembler update no longer executes/records the compensation pass inline");

            // GetComponentsFromConveyor can be entered by conveyor scheduling and keeps its
            // explicit game-thread hop; its inner mutation is still a single inline pass.
            var conveyor = PatchMethod("GetComponentsFromConveyorPatch");
            Assert.NotNull(conveyor);
            Assert.True(Calls(conveyor, "InvokeOnGameThread"));
            Assert.False(Calls(conveyor, "AddDelayedAction"));
        }

        [Theory]
        [InlineData("AfterUpdateProductionRefinery")]
        [InlineData("AfterUpdateProductionAssembler")]
        public void Vanilla_production_postfix_completes_the_exact_injected_batch(string methodName)
        {
            var postfix = PatchMethod(methodName);
            Assert.NotNull(postfix);
            Assert.True(Calls(postfix, "CompleteProductionPass", typeof(CompensationTracker).FullName));
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

        [Theory]
        [InlineData("AsyncUpdateAssemblerProduction")]
        [InlineData("FinishAssembling")]
        public void Assembler_mutation_helpers_do_not_swallow_partial_failures(string methodName)
        {
            var method = PatchMethod(methodName);
            Assert.NotNull(method);
            Assert.DoesNotContain(method.GetMethodBody().ExceptionHandlingClauses, c =>
                c.Flags == ExceptionHandlingClauseOptions.Clause ||
                c.Flags == ExceptionHandlingClauseOptions.Filter);
        }

        [Fact]
        public void Inactive_existing_block_cancels_pending_without_erasing_history()
        {
            var compensate = typeof(FreezeLogic).GetMethod("CompensateFrozenFrames",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(compensate);
            Assert.True(Calls(compensate, "CancelPending", typeof(CompensationTracker).FullName),
                "inactive live blocks must preserve taken/applied history");
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
            Assert.True(Calls(m, "Forget", typeof(CompensationTracker).FullName),
                "individual production-block removal must clear its compensation state");

            var clear = observer.GetMethod("ClearAll", BindingFlags.Static | BindingFlags.Public);
            Assert.NotNull(clear);
            Assert.True(Calls(clear, "ClearAll", typeof(CompensationTracker).FullName),
                "world unload must clear every compensation dictionary");
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
