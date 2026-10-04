using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using SentisOptimisationsPlugin;
using VRageMath;
using Xunit;

namespace SentisOptimisations.Tests;

/// <summary>
/// The navmesh cache's invalidation in one walk a box: the same cells removed as the game's quadratic loop, and the
/// game's members still where the patch looks for them.
/// </summary>
public class NavmeshCacheInvalidationTests
{
    /// <summary>The game's CheckCacheValidity, as decompiled: ElementAt from the start for every key.</summary>
    private static void GameWay(Dictionary<Vector3I, int> cache, List<(Vector3I Min, Vector3I Max)> boxes)
    {
        foreach (var item in boxes)
            for (var i = 0; i < cache.Count; i++)
            {
                var key = cache.ElementAt(i).Key;
                if (key.X >= item.Min.X && key.Y >= item.Min.Y && key.Z >= item.Min.Z && key.X <= item.Max.X && key.Y <= item.Max.Y && key.Z <= item.Max.Z)
                {
                    cache.Remove(key);
                    break;
                }
            }
    }

    private static Dictionary<Vector3I, int> Cache(Random random, int count)
    {
        var cache = new Dictionary<Vector3I, int>(1024, new Vector3I.EqualityComparer());
        while (cache.Count < count) cache[new Vector3I(random.Next(40), random.Next(10), random.Next(40))] = cache.Count;
        // (some removed and added again: the dictionary's order is not the order of insertion then)
        foreach (var key in cache.Keys.Where((k, i) => i % 7 == 0).ToList()) cache.Remove(key);
        while (cache.Count < count) cache[new Vector3I(random.Next(40), random.Next(10), random.Next(40))] = cache.Count;
        return cache;
    }

    [Fact]
    public void Removes_the_same_cells_as_the_game()
    {
        var random = new Random(7);
        for (var round = 0; round < 50; round++)
        {
            var cache = Cache(random, 600);
            var boxes = Enumerable.Range(0, 20).Select(_ =>
            {
                var min = new Vector3I(random.Next(40), random.Next(10), random.Next(40));
                return (min, min + new Vector3I(random.Next(4), random.Next(3), random.Next(4)));
            }).ToList();
            var game = new Dictionary<Vector3I, int>(cache, new Vector3I.EqualityComparer());
            GameWay(game, boxes);
            NavmeshCacheInvalidation.RemoveFirstInEachBox(cache, boxes);
            Assert.Equal(game.Keys.OrderBy(k => k.X).ThenBy(k => k.Y).ThenBy(k => k.Z), cache.Keys.OrderBy(k => k.X).ThenBy(k => k.Y).ThenBy(k => k.Z));
        }
    }

    [Fact]
    public void One_walk_a_box_on_a_big_cache()
    {
        // 20 000 cells, 30 boxes none of which holds a cell: the game's way walks 20 000²/2 steps a box
        var cache = new Dictionary<Vector3I, int>(new Vector3I.EqualityComparer());
        for (var i = 0; i < 20000; i++) cache[new Vector3I(i, 0, 0)] = i;
        var boxes = Enumerable.Range(0, 30).Select(i => (new Vector3I(-100 - i, 5, 5), new Vector3I(-90 - i, 6, 6))).ToList();
        var watch = Stopwatch.StartNew();
        NavmeshCacheInvalidation.RemoveFirstInEachBox(cache, boxes);
        Assert.True(watch.ElapsedMilliseconds < 200, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(20000, cache.Count);
    }

    [Fact]
    public void The_game_still_has_what_the_patch_uses()
    {
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = typeof(Sandbox.Game.AI.Pathfinding.RecastDetour.MyNavmeshManager).Assembly
            .GetType("Sandbox.Game.AI.Pathfinding.RecastDetour.MyNavigationInputMesh", true);
        Assert.NotNull(type.GetMethod("CheckCacheValidity", any, null, Type.EmptyTypes, null));
        Assert.NotNull(type.GetField("m_invalidateMeshCacheCoord", any));
        Assert.NotNull(type.GetField("m_meshCache", any));
        var interval = type.GetNestedType("CacheInterval", any);
        Assert.NotNull(interval?.GetField("Min", any));
        Assert.NotNull(interval?.GetField("Max", any));
    }
}
