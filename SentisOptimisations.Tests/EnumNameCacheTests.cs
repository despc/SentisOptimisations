using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Optimizer.Optimizations;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class EnumNameCacheTests
    {
        [Flags] private enum Bits { None = 0, A = 1, B = 2, C = 4 }
        private enum Plain : long { First = -5, Second = 7, Alias = 7 }

        private static readonly MethodInfo Format = EnumNameCache.Target();
        private static string Runtime(Enum value) =>
            (string)Format.Invoke(null, new object[] { value.GetType(), Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType())) });

        [Fact]
        public void The_patched_method_is_what_ToString_runs()
        {
            Assert.Equal(Plain.Second.ToString(), Runtime(Plain.Second));   // "Alias": the runtime picks among equal values itself
            Assert.Equal((Bits.A | Bits.C).ToString(), Runtime(Bits.A | Bits.C));
        }

        [Fact]
        public void Kept_texts_are_the_runtimes_own()
        {
            // every value the runtime names, plain, flags, aliases, numbers with no name, negative ones
            var values = new Enum[] { Plain.First, Plain.Second, Plain.Alias, (Plain)123, Bits.None, Bits.A, Bits.A | Bits.B | Bits.C, (Bits)64, (Bits)65, FileAccess.ReadWrite };
            foreach (var value in values)
            {
                var type = value.GetType();
                var raw = Convert.ChangeType(value, Enum.GetUnderlyingType(type));
                var text = Runtime(value);
                EnumNameCache.Keep(type, raw, text);
                Assert.True(EnumNameCache.TryGet(type, raw, out var kept));
                Assert.Equal(value.ToString(), kept);
            }
        }

        [Fact]
        public void The_same_number_of_another_enum_is_not_mixed_up()
        {
            EnumNameCache.Keep(typeof(Bits), 2, "B");
            Assert.True(EnumNameCache.TryGet(typeof(Bits), 2, out var b));
            Assert.Equal("B", b);
            Assert.False(EnumNameCache.TryGet(typeof(Plain), 2L, out _));
        }

        [Fact]
        public void A_type_keeps_at_most_PerType_values()
        {
            var type = typeof(DayOfWeek);
            for (var i = 0; i < EnumNameCache.PerType + 100; i++) EnumNameCache.Keep(type, 1000 + i, (1000 + i).ToString());
            var kept = Enumerable.Range(0, EnumNameCache.PerType + 100).Count(i => EnumNameCache.TryGet(type, 1000 + i, out _));
            Assert.Equal(EnumNameCache.PerType, kept);
        }
    }
}
