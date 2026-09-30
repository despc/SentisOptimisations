using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SentisOptimisationsPlugin;
using VRage.Collections;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class UpdateListBulkRemovalTests
    {
        private class A { }
        private class B { }
        private class C { }
        private class D { }

        private static T Get<T>(object list, string field) =>
            (T)list.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(list);

        private static object Make(Random random)
        {
            switch (random.Next(4))
            {
                case 0: return new A();
                case 1: return new B();
                case 2: return new C();
                default: return new D();
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void Removes_as_the_game_does_one_by_one(int seed)
        {
            var random = new Random(seed);
            var vanilla = new TypeSortedCachingList<object>();
            var bulk = new TypeSortedCachingList<object>();
            var items = Enumerable.Range(0, 400).Select(_ => Make(random)).ToList();
            foreach (var item in items)
            {
                vanilla.Add(item);
                bulk.Add(item);
            }
            vanilla.ApplyAdditions();
            bulk.ApplyAdditions();

            // Some twice, some never in the list, a whole type gone.
            var removed = items.Where(i => random.Next(3) == 0 || i is C).ToList();
            removed.Add(removed[0]);
            removed.Add(new A());
            removed.Add(new object());
            foreach (var item in removed)
            {
                vanilla.Remove(item);
                bulk.Remove(item);
            }

            vanilla.ApplyRemovals();
            UpdateListBulkRemoval.RemoveAll(Get<List<object>>(bulk, "m_list"), Get<List<object>>(bulk, "m_toRemove"),
                Get<Dictionary<Type, int>>(bulk, "m_typeIndexes"), Get<List<int>>(bulk, "m_sortIndexes"));
            Get<List<object>>(bulk, "m_toRemove").Clear();

            Assert.Equal(Get<List<object>>(vanilla, "m_list"), Get<List<object>>(bulk, "m_list"));
            Assert.Equal(Get<List<int>>(vanilla, "m_sortIndexes"), Get<List<int>>(bulk, "m_sortIndexes"));

            // Adding after it lands in the same places.
            var more = Enumerable.Range(0, 50).Select(_ => Make(random)).ToList();
            foreach (var item in more)
            {
                vanilla.Add(item);
                bulk.Add(item);
            }
            vanilla.ApplyAdditions();
            bulk.ApplyAdditions();
            Assert.Equal(Get<List<object>>(vanilla, "m_list"), Get<List<object>>(bulk, "m_list"));
        }

        [Fact]
        public void Many_removals_take_one_pass()
        {
            var list = new TypeSortedCachingList<object>();
            var items = Enumerable.Range(0, 200000).Select(i => i % 2 == 0 ? (object)new A() : new B()).ToList();
            foreach (var item in items) list.Add(item);
            list.ApplyAdditions();
            foreach (var item in items.Take(190000)) list.Remove(item);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            UpdateListBulkRemoval.RemoveAll(Get<List<object>>(list, "m_list"), Get<List<object>>(list, "m_toRemove"),
                Get<Dictionary<Type, int>>(list, "m_typeIndexes"), Get<List<int>>(list, "m_sortIndexes"));
            Assert.True(watch.ElapsedMilliseconds < 2000, "took " + watch.ElapsedMilliseconds + " ms");
            Assert.Equal(10000, list.Count);
        }
    }
}
