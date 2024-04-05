using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Havok;
using Profiler;
using Profiler.Basics;
using Profiler.Core;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations;
using SentisOptimisations.Utils;
using VRage.Game.Entity;
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin
{
    public sealed class PhysicsProfilerMonitor
    {
        
        public static PhysicsProfilerMonitor __instance = new PhysicsProfilerMonitor();
        private Punisher _punisher = Punisher.__instance;

        public static Task MoveToGameLoop(CancellationToken cancellationToken = default (CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<byte> taskSrc = new TaskCompletionSource<byte>();
            MyAPIGateway.Utilities.InvokeOnGameThread((Action) (() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    taskSrc.SetResult((byte) 0);
                }
                catch (Exception ex)
                {
                    taskSrc.SetException(ex);
                }
            }));
            return (Task) taskSrc.Task;
        }
        
        public async Task Profile()
        {
            if (!SentisOptimisationsPlugin.Config.EnablePhysicsGuard)
            {
                return;
            }
            if (SentisOptimisationsPlugin.Instance.AllGridsProcessor.CancellationTokenSource.Token.IsCancellationRequested)
            {
                return;
            }

            // var profilerEnabled = ProfilerConfig.Instance.Enabled;
            // bool needDisable = !profilerEnabled;
            // if (!profilerEnabled)
            // {
            //     ProfilerConfig.Instance.Enabled = true;
            // }
            using (var profiler = new PhysicsProfiler())
            using (ProfilerResultQueue.Profile(profiler))
            {
                await MoveToGameLoop();

                profiler.MarkStart();

                for (var i = 0; i < 10; i++)
                {
                    await MoveToGameLoop();
                }

                profiler.MarkEnd();

                var result = profiler.GetResult();
                ProcessResult(result);
            }
            // if (needDisable)
            // {
            //     ProfilerConfig.Instance.Enabled = false;
            // }
        }

        void ProcessResult(BaseProfilerResult<HkWorld> result)
        {
            foreach (var (world, entity) in result.GetTopEntities(10))
            {
                // this usually doesn't happen but just in case
                if (!TryGetHeaviestGridGroup(world, out var heaviestGrids)) continue;
                
                var mainMs = entity.MainThreadTime / result.TotalFrameCount;
                
                if (mainMs > SentisOptimisationsPlugin.Config.PhysicsMsToPunishImmediately)
                {
                    _punisher.PunishPlayerGridImmediately(heaviestGrids);
                }
                
                if (mainMs > SentisOptimisationsPlugin.Config.PhysicsMsToPunish)
                {
                    _punisher.PunishPlayerGrid(heaviestGrids);
                }
                
                if (mainMs > SentisOptimisationsPlugin.Config.PhysicsMsToAlert)
                {
                    _punisher.AlertPlayerGrid(heaviestGrids);
                }

                
            }
        }

        static bool TryGetHeaviestGridGroup(HkWorld world, out List<IMyCubeGrid> heaviestGridGroup)
        {
            var grids = PhysicsUtils.GetEntities(world)
                .Where(e => e is IMyCubeGrid)
                .Where(e => !e.Physics.IsStatic)
                .Cast<IMyCubeGrid>()
                .Where(e => PhysicsUtils.IsTopMostParent<IMyCubeGrid>((MyEntity) e))
                .ToArray();

            if (!grids.Any())
            {
                heaviestGridGroup = null;
                return false;
            }

            List<List<IMyCubeGrid>> groups = new List<List<IMyCubeGrid>>();
            List<IMyCubeGrid> gridsToCollectGroup = new List<IMyCubeGrid>(grids);
            while (gridsToCollectGroup.Count > 0)
            {
                var grid = gridsToCollectGroup[0];
                List<IMyCubeGrid> group = new List<IMyCubeGrid>();
                MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Physical, group);
                group.ForEach(cubeGrid => gridsToCollectGroup.Remove(cubeGrid));
                groups.Add(group);
            }
            
            heaviestGridGroup = groups.OrderByDescending(gr =>
            {
                float totalMass = 0;
                foreach (var myCubeGrid in gr)
                {
                    if (myCubeGrid.IsStatic)
                    {
                        continue;
                    }
                    if (myCubeGrid.Physics == null)
                    {
                        continue;
                    }

                    if (myCubeGrid.Physics.RigidBody.GetMotionType() == HkMotionType.Fixed)
                    {
                        continue;
                    }
                    totalMass += myCubeGrid.Physics.Mass;
                }
                return totalMass;
            }).First();
            return true;
        }
    }
}