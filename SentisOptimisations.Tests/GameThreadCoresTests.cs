using System.Collections.Generic;
using System.Linq;
using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests
{
    /// <summary>Which logical processors the game thread is kept on.</summary>
    public class GameThreadCoresTests
    {
        private static GameThreadCores.CpuSet Set(byte logical, byte core, byte efficiency, byte scheduling) =>
            new GameThreadCores.CpuSet { Id = 256u + logical, Logical = logical, Core = core, Efficiency = efficiency, Scheduling = scheduling };

        /// <summary>i7-13700K: 8 P-cores with SMT (8-11 favored), 8 E-cores.</summary>
        private static List<GameThreadCores.CpuSet> I7_13700K()
        {
            var sets = new List<GameThreadCores.CpuSet>();
            for (byte i = 0; i < 16; i++) sets.Add(Set(i, (byte)(i & ~1), 1, (byte)(i >= 8 && i <= 11 ? 2 : 1)));
            for (byte i = 16; i < 24; i++) sets.Add(Set(i, i, 0, 0));
            return sets;
        }

        private static byte[] Logical(List<GameThreadCores.CpuSet> sets) => sets?.Select(s => s.Logical).ToArray();

        [Fact]
        public void Favored_performance_cores_one_logical_each() =>
            Assert.Equal(new byte[] { 8, 10 }, Logical(GameThreadCores.Choose(I7_13700K())));

        [Fact]
        public void One_favored_core_takes_the_next_class_too()
        {
            var sets = I7_13700K().Select(s => s.Logical == 10 || s.Logical == 11 ? Set(s.Logical, s.Core, 1, 1) : s).ToList();
            Assert.Equal(new byte[] { 8, 0, 2, 4, 6, 10, 12, 14 }, Logical(GameThreadCores.Choose(sets)));
        }

        [Fact]
        public void Hybrid_without_favored_cores_keeps_off_efficiency_cores()
        {
            var sets = I7_13700K().Select(s => s.Efficiency == 1 ? Set(s.Logical, s.Core, 1, 1) : s).ToList();
            Assert.Equal(new byte[] { 0, 2, 4, 6, 8, 10, 12, 14 }, Logical(GameThreadCores.Choose(sets)));
        }

        [Fact]
        public void Cores_all_alike_are_left_alone()
        {
            var sets = Enumerable.Range(0, 16).Select(i => Set((byte)i, (byte)(i & ~1), 0, 0)).ToList();
            Assert.Null(GameThreadCores.Choose(sets));
        }

        [Fact]
        public void Sets_allocated_to_another_process_are_not_used()
        {
            var sets = I7_13700K().Select(s => { if (s.Logical == 8 || s.Logical == 9) s.Allocated = true; return s; }).ToList();
            Assert.DoesNotContain((byte)8, Logical(GameThreadCores.Choose(sets)));
            Assert.Contains((byte)10, Logical(GameThreadCores.Choose(sets)));
        }
    }
}
