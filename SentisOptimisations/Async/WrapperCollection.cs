using System;
using System.Collections.Generic;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin.Async;

public class WrapperCollection
{
    private Dictionary<Type, UpdateEntityWrapper> wrappers = new Dictionary<Type, UpdateEntityWrapper>();

    public void AddWrapper(UpdateEntityWrapper wrapper)
    {
        wrappers[wrapper.EntityType] = wrapper;
    }
    
    public UpdateEntityWrapper GetWrapperByInstance(MyEntity entity)
    {
        var entityType = entity.GetType();

        if (wrappers.TryGetValue(entityType, out var wrapper))
        {
            return wrapper;
        }

        return null;
    }
}