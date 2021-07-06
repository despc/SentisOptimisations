using System;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class ConveyorPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
  
             var MethodOnBlockAdded = typeof(MyCubeGridSystems).GetMethod
                 (nameof(MyCubeGridSystems.OnBlockAdded), BindingFlags.Instance | BindingFlags.Public);

            
             ctx.GetPattern(MethodOnBlockAdded).Prefixes.Add(
                 typeof(ConveyorPatch).GetMethod(nameof(MethodOnBlockAddedPatched),
                     BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
             
             var MethodOnBlockRemoved = typeof(MyCubeGridSystems).GetMethod
                 (nameof(MyCubeGridSystems.OnBlockRemoved), BindingFlags.Instance | BindingFlags.Public);

            
             ctx.GetPattern(MethodOnBlockRemoved).Prefixes.Add(
                 typeof(ConveyorPatch).GetMethod(nameof(MethodOnBlockRemovedPatched),
                     BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool MethodOnBlockAddedPatched(MyCubeGridSystems __instance, MySlimBlock block)
        {
            if (block.FatBlock is MyThrust)
            {
                if (__instance.ShipSoundComponent != null)
                    __instance.ShipSoundComponent.ShipHasChanged = true;
                MyCubeGrid m_cubeGrid = (MyCubeGrid) GetInstanceField(__instance.GetType(), __instance, "m_cubeGrid");
                m_cubeGrid.Components.Get<MyEntityThrustComponent>()?.MarkDirty();
            }

            if (__instance.ConveyorSystem != null && block.FatBlock is IMyConveyorEndpointBlock)
            {
                __instance.ConveyorSystem.UpdateLines();
            }
            return false;
        }
        
        private static bool MethodOnBlockRemovedPatched(MyCubeGridSystems __instance, MySlimBlock block)
        {
            if (block.FatBlock is MyThrust)
            {
                if (__instance.ShipSoundComponent != null)
                    __instance.ShipSoundComponent.ShipHasChanged = true;
                MyCubeGrid m_cubeGrid = (MyCubeGrid) GetInstanceField(__instance.GetType(), __instance, "m_cubeGrid");
                m_cubeGrid.Components.Get<MyEntityThrustComponent>()?.MarkDirty();
            }

            if (__instance.ConveyorSystem != null && block.FatBlock is IMyConveyorEndpointBlock)
            {
                __instance.ConveyorSystem.UpdateLines();
            }
            return false;
        }
        
        private static object GetInstanceField(Type type, object instance, string fieldName)
        {
            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Static;
            FieldInfo field = type.GetField(fieldName, bindFlags);
            return field.GetValue(instance);
        }
    }
    
    
}