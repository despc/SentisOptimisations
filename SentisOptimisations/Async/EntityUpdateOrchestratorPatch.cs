using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin.Async
{
    [PatchShim]
    public static class EntityUpdateOrchestratorPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var MethodAddEntity = typeof(MyParallelEntityUpdateOrchestrator).GetMethod
                (nameof(MyParallelEntityUpdateOrchestrator.AddEntity), BindingFlags.Instance | BindingFlags.Public);


            ctx.GetPattern(MethodAddEntity).Prefixes.Add(
                typeof(EntityUpdateOrchestratorPatch).GetMethod(nameof(AddEntityPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodRemoveEntity = typeof(MyParallelEntityUpdateOrchestrator).GetMethod
                (nameof(MyParallelEntityUpdateOrchestrator.RemoveEntity), BindingFlags.Instance | BindingFlags.Public);


            ctx.GetPattern(MethodRemoveEntity).Prefixes.Add(
                typeof(EntityUpdateOrchestratorPatch).GetMethod(nameof(RemoveEntityPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static void AddEntityPatched(MyEntity entity)
        {
            if (entity is MyThrust)
            {
                AsyncUpdater.DistributedUpdaterAfter10.Add(entity);
                AsyncUpdater.DistributedUpdaterAfter100.Add(entity);
            }
            if (entity is MyFunctionalBlock)
            {
                var textPanelComponent = MyTextPanelWrapper30._multiPanel.GetValue(entity) as MyMultiTextPanelComponent;
                if (textPanelComponent != null)
                {
                    AsyncUpdater.DistributedUpdaterAfter30.Add(entity);
                }
                
            }
        }
        
        private static void RemoveEntityPatched(MyEntity entity)
        {
            if (entity is MyThrust)
            {
                AsyncUpdater.DistributedUpdaterAfter10.Remove(entity);
                AsyncUpdater.DistributedUpdaterAfter100.Remove(entity);
            }
            if (entity is MyFunctionalBlock)
            {
                var textPanelComponent = MyTextPanelWrapper30._multiPanel.GetValue(entity) as MyMultiTextPanelComponent;
                if (textPanelComponent != null)
                {
                    AsyncUpdater.DistributedUpdaterAfter30.Remove(entity);
                }
            }
        }
    }
}