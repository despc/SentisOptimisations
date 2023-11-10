﻿using System.Reflection;
using Sandbox;
using Sandbox.Game.Entities.Cube;
using SentisOptimisationsPlugin.CrashFix;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin.Freezer;

[PatchShim]
public static class FreezerPatches
{
    public static void Patch(PatchContext ctx)
    {
        var MethodGetFramesFromLastTrigger = typeof(MyFunctionalBlock).GetMethod
        (nameof(MyFunctionalBlock.GetFramesFromLastTrigger),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);


        ctx.GetPattern(MethodGetFramesFromLastTrigger).Prefixes.Add(
            typeof(FreezerPatches).GetMethod(nameof(PatchGetFramesFromLastTrigger),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
    }

    private static bool PatchGetFramesFromLastTrigger(MyFunctionalBlock __instance, ref uint __result)
    {
        var gridEntityId = __instance.CubeGrid.EntityId;
        if (FreezeLogic.LastUpdateFrames.TryGetValue(gridEntityId, out var lastUpdateFrame))
        {
            __result = (uint)(MySandboxGame.Static.SimulationFrameCounter - lastUpdateFrame);
            FreezeLogic.LastUpdateFrames.Remove(gridEntityId);
            return false;
        }

        return true;
    }
}