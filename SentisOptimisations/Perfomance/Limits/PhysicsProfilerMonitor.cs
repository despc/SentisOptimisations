using System.Linq;
using System.Threading.Tasks;
using Havok;
using Profiler.Basics;
using Profiler.Core;
using Profiler.Utils;
using Sandbox.Game.World;
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
        // static readonly ILogger Log = LogManager.GetCurrentClassLogger();
        // readonly IConfig _physicsConfig;
        //
        // public PhysicsProfilerMonitor(IMonitorGeneralConfig config, IConfig physicsConfig)
        // {
        //     _config = config;
        //     _physicsConfig = physicsConfig;
        // }
        //
        // public void OnInterval(int intervalsSinceStart)
        // {
        //     if (!_physicsConfig.PhysicsEnabled) return;
        //     if (intervalsSinceStart < _config.FirstIgnoredSeconds) return;
        //     if (intervalsSinceStart % _physicsConfig.PhysicsInterval != 0) return;
        //
        //     Profile().Forget(Log);
        // }

        public async Task Profile()
        {
            using (var profiler = new PhysicsProfiler())
            using (ProfilerResultQueue.Profile(profiler))
            {
                await GameLoopObserver.MoveToGameLoop();

                profiler.MarkStart();

                for (var i = 0; i < 10; i++)
                {
                    await GameLoopObserver.MoveToGameLoop();
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