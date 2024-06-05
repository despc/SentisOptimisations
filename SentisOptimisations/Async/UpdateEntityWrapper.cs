using System;
using HarmonyLib;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin.Async;

public class UpdateEntityWrapper
{
    public static Harmony HarmonyInstance = new Harmony("UpdateEntityWrapper");

    public Type EntityType;

    public virtual void Update(MyEntity entity)
    {
    }
    
    public static bool Disabled()
    {
        if (!SentisOptimisationsPlugin.Config.AsyncLogicUpdateMain)
        {
            return true;
        }
        return false;
    }
}