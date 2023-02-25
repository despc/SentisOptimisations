using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Havok;
using Profiler.Basics;
using Profiler.Core;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations;
using SentisOptimisations.Utils;
using VRage.Game.Entity;
using VRage.Game.ModAPI;

namespace TorchMonitor.ProfilerMonitors
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
            if (!SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.EnablePhysicsGuard)
            {
                return;
            }
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
        }

        void ProcessResult(BaseProfilerResult<HkWorld> result)
        {
            foreach (var (world, entity) in result.GetTopEntities(10))
            {
                // this usually doesn't happen but just in case
                if (!TryGetHeaviestGrid(world, out var heaviestGrid)) continue;
                var ownerId = PlayerUtils.GetOwner(heaviestGrid);
                var faction = MySession.Static.Factions.GetPlayerFaction(ownerId);
                var factionTag = faction?.Tag ?? "<n/a>";
                var gridName = heaviestGrid.DisplayName;
                var mainMs = entity.MainThreadTime / result.TotalFrameCount;
                
                if (mainMs > SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.PhysicsMsToPunishImmediately)
                {
                    _punisher.PunishPlayerGridImmediately(heaviestGrid);
                }
                
                if (mainMs > SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.PhysicsMsToPunish)
                {
                    _punisher.PunishPlayerGrid(heaviestGrid);
                }
                
                if (mainMs > SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.PhysicsMsToAlert)
                {
                    _punisher.AlertPlayerGrid(heaviestGrid);
                }

                
            }
        }

        static bool TryGetHeaviestGrid(HkWorld world, out IMyCubeGrid heaviestGrid)
        {
            var grids = PhysicsUtils.GetEntities(world)
                .Where(e => e is IMyCubeGrid)
                .Cast<IMyCubeGrid>()
                .Where(e => PhysicsUtils.IsTopMostParent<IMyCubeGrid>((MyEntity) e))
                .ToArray();

            if (!grids.Any())
            {
                heaviestGrid = null;
                return false;
            }

            heaviestGrid = grids.OrderByDescending(g => g.Physics.Mass).First();
            return true;
        }
    }
}