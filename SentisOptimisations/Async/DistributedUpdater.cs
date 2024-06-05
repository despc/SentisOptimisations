using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin.Async;

public class DistributedUpdater
{
    private readonly int _step;
    private int _position;

    private ConcurrentDictionary<int, ConcurrentBag<MyEntity>> _entitiesToUpdate = new ConcurrentDictionary<int, ConcurrentBag<MyEntity>>();

    public WrapperCollection WrappersAfter = new WrapperCollection();
    // private Dictionary<Type, UpdateEntityWrapper> WrappersBefore;
    public DistributedUpdater(int step)
    {
        _step = step;
    }

    public void Update()
    {
        if (_position >= _step)
        {
            _position = 0;
        }
        if (_entitiesToUpdate.TryGetValue(_position, out var entities))
        {
            foreach (var myEntity in entities)
            {
                // if (WrappersBefore.TryGetValue(myEntity.GetType(), out var wrapper))
                // {
                //     wrapper.UpdateBefore();
                // }
                var wrapper = WrappersAfter.GetWrapperByInstance(myEntity);
                wrapper?.Update(myEntity);
            }
        }
        _position++;
    }
    
    public void Add(MyEntity entity)
    {
        int bucket = (int)(entity.EntityId % _step);
        var entities = _entitiesToUpdate.GetOrAdd(bucket, _ => new ConcurrentBag<MyEntity>());
        entities.Add(entity);
    }
    
    public void Remove(MyEntity entity)
    {
        int bucket = (int)(entity.EntityId % _step);
        lock (_entitiesToUpdate)
        {
            if (_entitiesToUpdate.TryGetValue(bucket, out var entities))
            {
                entities.TryTake(out entity); // ConcurrentBag doesn't support removal by value directly
            }
            else
            {
                SentisOptimisationsPlugin.Log.Error($"Cant find entity in DistributedUpdater to remove {entity.DisplayName}");
            }
        }
    }
    // public void Update();
}