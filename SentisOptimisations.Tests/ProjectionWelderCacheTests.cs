using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SentisOptimisations.Slowdowns;
using SpaceEngineers.Game.Entities.Blocks;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class ProjectionWelderCacheTests
    {
        [Fact]
        public void Replacing_a_welder_invalidates_the_cache_even_at_the_same_count()
        {
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var grid = (MyCubeGrid)FormatterServices.GetUninitializedObject(typeof(MyCubeGrid));
            var oldWelder = (MyShipWelder)FormatterServices.GetUninitializedObject(typeof(MyShipWelder));
            var newWelder = (MyShipWelder)FormatterServices.GetUninitializedObject(typeof(MyShipWelder));
            var fat = typeof(MySlimBlock).GetProperty("FatBlock");
            var oldBlock = (MySlimBlock)FormatterServices.GetUninitializedObject(typeof(MySlimBlock));
            var newBlock = (MySlimBlock)FormatterServices.GetUninitializedObject(typeof(MySlimBlock));
            fat.SetValue(oldBlock, oldWelder);
            fat.SetValue(newBlock, newWelder);
            var blocks = new HashSet<MySlimBlock> { oldBlock };
            typeof(MyCubeGrid).GetField("m_cubeBlocks", fields).SetValue(grid, blocks);

            var type = typeof(ProjectionSlowdown).GetNestedType("GridWelders", BindingFlags.NonPublic);
            var cache = Activator.CreateInstance(type, grid);
            var get = type.GetMethod("Get");
            List<MyShipWelder> ReadCache(int count) => (List<MyShipWelder>)get.Invoke(cache, new object[] { grid, count });
            var first = ReadCache(1);
            Assert.Same(oldWelder, Assert.Single(first));

            blocks.Remove(oldBlock);
            blocks.Add(newBlock);
            Assert.Same(oldWelder, Assert.Single(ReadCache(1)));
            var change = (Action<MySlimBlock>)typeof(MyCubeGrid).GetField("OnBlockAdded", fields).GetValue(grid);
            change(newBlock);
            Assert.Same(newWelder, Assert.Single(ReadCache(1)));

            blocks.Remove(newBlock);
            Assert.Empty(ReadCache(0));
        }
    }
}
