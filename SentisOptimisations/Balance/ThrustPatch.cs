using System;
using System.Collections.Generic;
using System.Reflection;
using Havok;
using NLog;
using ParallelTasks;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class ThrustPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var assembly = typeof(MyProgrammableBlock).Assembly;
            var typeMyThrusterBlockThrustComponent =
                assembly.GetType("Sandbox.Game.EntityComponents.MyThrusterBlockThrustComponent");

            var Init = typeof(MyThrustDefinition).GetMethod
                ("Init", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            ctx.GetPattern(Init).Suffixes.Add(
                typeof(ThrustPatch).GetMethod(nameof(InitPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        //
        private static void InitPatched(MyThrustDefinition __instance)
        {
            try
            {
                __instance.ForceMagnitude = __instance.ForceMagnitude * SentisOptimisationsPlugin.Config.ThrustPowerMultiplier;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn("InitPatched Exception ", e);
            }
        }
    }
}