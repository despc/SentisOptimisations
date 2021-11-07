using System;
using System.Reflection;
using Havok;
using NLog;
using ParallelTasks;
using Sandbox.Engine.Physics;
using Sandbox.Game.Weapons;
using SentisOptimisations;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
     [PatchShim]
    public static class WelderPatch
    {
    public static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static Random r = new Random();
    
    public static void Patch(PatchContext ctx)
    {
        var MyPhysicsLoadData = typeof(MyShipWelder).GetMethod
            ("FindProjectedBlocks", BindingFlags.Instance | BindingFlags.NonPublic);
    
        ctx.GetPattern(MyPhysicsLoadData).Prefixes.Add(
            typeof(WelderPatch).GetMethod(nameof(FindProjectedBlocksPatched),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        
    }
    
    private static bool FindProjectedBlocksPatched(ref MyWelder.ProjectionRaycastData[] __result)
    {
        try
        {
            var pullItemsSlowdown = SentisOptimisationsPlugin.Config.FindProjectedBlocksSlowdown;
            var chance = 1 / pullItemsSlowdown;
            var run = r.NextDouble() <= chance;
            if (!run)
            {
                __result = new MyWelder.ProjectionRaycastData[0];
            }
            return run;
        }
        catch (Exception e)
        {
            SentisOptimisationsPlugin.Log.Warn("MyPhysicsLoadDataPatched Exception ", e);
        }

        return true;
    }
    }
}