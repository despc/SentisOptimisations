using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using ParallelTasks;
using Sandbox.Game.Entities;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRage.Collections;
using VRage.Game.Entity;
using VRage.ObjectBuilders;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class SavePatch
    {
        public static void Patch(PatchContext ctx)
        {
            // var MethodSave = typeof(MyEntities).GetMethod
            //     ("Save", BindingFlags.Static | BindingFlags.NonPublic);
            //
            // ctx.GetPattern(MethodSave).Prefixes.Add(
            //     typeof(SavePatch).GetMethod(nameof(SavePatched),
            //         BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        // private static bool SavePatched(ref List<MyObjectBuilder_EntityBase> __result)
        // {
        //     MyConcurrentHashSet<MyEntity> entities = (MyConcurrentHashSet<MyEntity>)
        //         ReflectionUtils.GetPrivateStaticField(typeof(MyEntities), "m_entities");
        //     HashSet<MyEntity> entitiesToDelete = (HashSet<MyEntity>)
        //         ReflectionUtils.GetPrivateStaticField(typeof(MyEntities), "m_entitiesToDelete");
        //     List<MyObjectBuilder_EntityBase> builderEntityBaseList = new List<MyObjectBuilder_EntityBase>();
        //     if (!SentisOptimisationsPlugin.Config.EnableAsyncSave)
        //     {
        //         Stopwatch stopWatch = new Stopwatch();
        //         stopWatch.Start();
        //         foreach (MyEntity entity in entities)
        //         {
        //             if (entity.Save && !entitiesToDelete.Contains(entity) && !entity.MarkedForClose)
        //             {
        //                 entity.BeforeSave();
        //                 MyObjectBuilder_EntityBase objectBuilder = entity.GetObjectBuilder(false);
        //                 builderEntityBaseList.Add(objectBuilder);
        //             }
        //         }
        //
        //         __result = builderEntityBaseList;
        //         SentisOptimisationsPlugin.Log.Info($"Default save time - {stopWatch.ElapsedMilliseconds}");
        //         return false;
        //     }
        //     Stopwatch stopWatchAsync = new Stopwatch();
        //     stopWatchAsync.Start();
        //     HashSet<MyCubeGrid> cubeGrids = new HashSet<MyCubeGrid>();
        //     foreach (MyEntity entity in entities)
        //     {
        //         if (entity.Save && !entitiesToDelete.Contains(entity) && !entity.MarkedForClose)
        //         {
        //             if (entity is MyCubeGrid)
        //             {
        //                 cubeGrids.Add((MyCubeGrid)entity);
        //                 continue;
        //             }
        //             entity.BeforeSave();
        //             MyObjectBuilder_EntityBase objectBuilder = entity.GetObjectBuilder(false);
        //             builderEntityBaseList.Add(objectBuilder);
        //         }
        //     }
        //     SentisOptimisationsPlugin.Log.Info($"Async save non grids time - {stopWatchAsync.ElapsedMilliseconds}");
        //     
        //     List<MyObjectBuilder_EntityBase> cubeGridsOB = new List<MyObjectBuilder_EntityBase>();
        //     Parallel.ForEach<MyCubeGrid>(cubeGrids, grid =>
        //     {
        //         grid.BeforeSave();
        //         MyObjectBuilder_EntityBase objectBuilder = grid.GetObjectBuilder(false);
        //         lock (cubeGridsOB)
        //         {
        //             cubeGridsOB.Add(objectBuilder); 
        //         }
        //     });
        //     builderEntityBaseList.AddRange(cubeGridsOB);
        //     __result = builderEntityBaseList;
        //     SentisOptimisationsPlugin.Log.Info($"Async save grids time - {stopWatchAsync.ElapsedMilliseconds}");
        //     return false;
        // }

    }
}