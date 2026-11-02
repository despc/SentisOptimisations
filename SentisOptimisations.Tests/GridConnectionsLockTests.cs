using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Optimizer.Optimizations;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Weapons;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class GridConnectionsLockTests
    {
        [Fact]
        public void Real_game_IL_changes_only_the_UpdateGridConnections_call()
        {
            var method = typeof(MyLargeTurretTargetingSystem).GetMethod("CheckAndSelectNearTargetsParallel");
            var original = PatchUtilities.ReadInstructions(method).ToList();
            var transpiler = typeof(GridConnectionsLock).GetMethod("Transpiler", BindingFlags.Static | BindingFlags.NonPublic);
            var output = ((IEnumerable<MsilInstruction>)transpiler.Invoke(null, new object[] { original.ToList() })).ToList();
            var update = typeof(MyGridTargeting).GetMethod("UpdateGridConnections");
            var locked = typeof(GridConnectionsLock).GetMethod("UpdateLocked");
            Assert.Equal(original.Count, output.Count);
            var replaced = 0;
            for (var i = 0; i < original.Count; i++)
            {
                if (original[i].Operand is MsilOperandInline<MethodBase> op && op.Value == update)
                {
                    Assert.Equal(OpCodes.Call, output[i].OpCode);
                    Assert.Equal(locked, ((MsilOperandInline<MethodBase>)output[i].Operand).Value);
                    Assert.Equal(original[i].Labels, output[i].Labels);
                    replaced++;
                }
                else Assert.Same(original[i], output[i]);
            }
            Assert.Equal(1, replaced);
            // the static call takes what the instance call took, in the same order
            Assert.Equal(new[] { typeof(MyGridTargeting) }.Concat(update.GetParameters().Select(p => p.ParameterType)),
                locked.GetParameters().Select(p => p.ParameterType));
        }

        [Fact]
        public void The_call_holds_the_components_lock_and_lets_it_go_on_a_throw()
        {
            // an uninitialised component: the game's method throws inside (no session), the lock must be let go
            var targeting = (MyGridTargeting)FormatterServices.GetUninitializedObject(typeof(MyGridTargeting));
            Assert.ThrowsAny<Exception>(() => GridConnectionsLock.UpdateLocked(targeting, new List<List<long>>(), new List<long>(), null));
            Assert.True(Task.Run(() =>
            {
                if (!Monitor.TryEnter(targeting, 0)) return false;
                Monitor.Exit(targeting);
                return true;
            }).Result, "the component's lock was left held after the throw");
        }

        [Theory]
        [InlineData(100, int.MinValue, false)]   // never made
        [InlineData(100, 100, true)]             // made this frame
        [InlineData(109, 100, true)]             // 9 frames on
        [InlineData(110, 100, false)]            // KeepFrames on: made anew
        [InlineData(50, 100, false)]             // a new session's counter is behind: made anew
        public void A_list_is_kept_for_KeepFrames(int now, int made, bool fresh)
        {
            Assert.Equal(10, GridConnectionsLock.KeepFrames);
            Assert.Equal(fresh, GridConnectionsLock.Fresh(now, made));
        }

        [Fact]
        public void The_frame_field_the_game_checks_is_there()
        {
            var field = typeof(MyGridTargeting).GetField("m_gridGroupCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            Assert.Equal(typeof(int), field.FieldType);
        }

        private sealed class Block { public int Id; }

        [Fact]
        public void Scheduler_registers_from_many_threads_without_losing_one()
        {
            var type = typeof(TurretControlTargetSelect).Assembly.GetType("Optimizer.Optimizations.TurretSearchScheduler`1").MakeGenericType(typeof(Block));
            var scheduler = Activator.CreateInstance(type);
            var register = (Action<Block, long>)Delegate.CreateDelegate(typeof(Action<Block, long>), scheduler, type.GetMethod("Register"));
            var tick = (Action<ulong, Action<Block>>)Delegate.CreateDelegate(typeof(Action<ulong, Action<Block>>), scheduler, type.GetMethod("Tick"));
            var blocks = Enumerable.Range(0, 20000).Select(i => new Block { Id = i }).ToArray();
            Parallel.ForEach(blocks, new ParallelOptions { MaxDegreeOfParallelism = 8 }, b => { register(b, b.Id); register(b, b.Id); });
            var seen = new List<Block>();
            for (ulong frame = 0; frame < (ulong)TurretControlTargetSelect.Period; frame++) tick(frame, seen.Add);
            Assert.Equal(blocks.Length, seen.Count);
            Assert.Equal(blocks.Length, seen.Distinct().Count());
            GC.KeepAlive(blocks);
        }
    }
}
