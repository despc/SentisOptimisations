using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Havok;
using NLog;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations;
using SentisOptimisations.Utils;
using VRage.Game.Entity;
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// Samples Havok physics for a few frames through the "Profiler" Torch plugin and punishes/alerts
    /// the heaviest grid group. The Profiler assembly used to be a compile-time reference; it now binds
    /// lazily so this plugin loads even when Profiler.dll is absent (feature simply stays inactive).
    /// </summary>
    public sealed class PhysicsProfilerMonitor
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static PhysicsProfilerMonitor __instance = new PhysicsProfilerMonitor();
        private Punisher _punisher = Punisher.__instance;

        private const string PhysicsProfilerTypeName = "Profiler.Basics.PhysicsProfiler";
        private const string ProfilerResultQueueTypeName = "Profiler.Core.ProfilerResultQueue";

        private bool _profilerLookedUp;
        private Type _physicsProfilerType;
        private Type _profilerResultQueueType;
        private bool _warnedOnce;

        private void ResolveProfilerTypes()
        {
            if (_profilerLookedUp) return;
            _profilerLookedUp = true;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var profilerType = assembly.GetType(PhysicsProfilerTypeName);
                var queueType = assembly.GetType(ProfilerResultQueueTypeName);
                if (profilerType == null || queueType == null) continue;

                _physicsProfilerType = profilerType;
                _profilerResultQueueType = queueType;
                Log.Info("Profiler assembly detected, physics profiling enabled.");
                return;
            }
        }

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

            ResolveProfilerTypes();
            if (_physicsProfilerType == null)
            {
                if (!_warnedOnce)
                {
                    _warnedOnce = true;
                    Log.Warn("Profiler.dll (Profiler plugin) not found; physics profiling guard is inactive.");
                }
                return;
            }

            object profiler = null;
            try
            {
                profiler = Activator.CreateInstance(_physicsProfilerType);

                // Equivalent to the "using (ProfilerResultQueue.Profile(profiler))" scope:
                // static-int Void AddProfiler(IProfiler) / RemoveProfiler(IProfiler).
                var addProfiler = _profilerResultQueueType.GetMethod("AddProfiler",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var removeProfiler = _profilerResultQueueType.GetMethod("RemoveProfiler",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (addProfiler == null || removeProfiler == null)
                    throw new MissingMethodException("ProfilerResultQueue.AddProfiler/RemoveProfiler");

                addProfiler.Invoke(null, new[] {profiler});
                try
                {
                    await MoveToGameLoop();

                    _physicsProfilerType.GetMethod("MarkStart").Invoke(profiler, null);

                    for (var i = 0; i < 10; i++)
                    {
                        await MoveToGameLoop();
                    }

                    _physicsProfilerType.GetMethod("MarkEnd").Invoke(profiler, null);

                    var getResult = _physicsProfilerType.GetMethod("GetResult");
                    if (getResult == null)
                        throw new MissingMethodException("PhysicsProfiler.GetResult");

                    var result = getResult.Invoke(profiler, null);
                    ProcessResult(result);
                }
                finally
                {
                    removeProfiler.Invoke(null, new[] {profiler});
                }
            }
            catch (Exception e)
            {
                if (!_warnedOnce)
                {
                    _warnedOnce = true;
                    Log.Error(e, "Physics profiling via Profiler.dll failed; guard disabled to avoid repeated errors.");
                }
            }
            finally
            {
                (profiler as IDisposable)?.Dispose();
            }
        }

        /// <summary>
        /// result is Profiler.Basics.BaseProfilerResult&lt;HkWorld&gt; obtained via reflection.
        /// result.GetTopEntities(int) yields KeyedEntity { HkWorld Key; ProfilerEntry Entity; }
        /// </summary>
        void ProcessResult(object result)
        {
            var getTopEntities = result.GetType().GetMethod("GetTopEntities");
            if (getTopEntities == null) return;

            var totalFrameCountProp = result.GetType().GetProperty("TotalFrameCount");
            if (totalFrameCountProp == null) return;
            var totalFrameCount = Convert.ToUInt64(totalFrameCountProp.GetValue(result));
            if (totalFrameCount == 0) return;

            var top = (IEnumerable) getTopEntities.Invoke(result, new object[] {10});
            foreach (var keyedEntity in top)
            {
                var key = GetMember(keyedEntity, "Key") as HkWorld;
                var entry = GetMember(keyedEntity, "Entity");
                if (key == null || entry == null) continue;

                // this usually doesn't happen but just in case
                if (!TryGetHeaviestGridGroup(key, out var heaviestGrids)) continue;

                var mainThreadTime = Convert.ToDouble(GetMember(entry, "MainThreadTime"));
                var mainMs = mainThreadTime / totalFrameCount;
                
                if (mainMs > SentisOptimisationsPlugin.Config.PhysicsMsToPunishImmediately)
                {
                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    {
                        try
                        {
                            _punisher.PunishPlayerGridImmediately(heaviestGrids);
                        }
                        catch
                        {
                        }
                    });
                }
                
                if (mainMs > SentisOptimisationsPlugin.Config.PhysicsMsToPunish)
                {
                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    {
                        try
                        {
                            _punisher.PunishPlayerGrid(heaviestGrids);
                        }
                        catch
                        {
                        }
                    });
                    
                }
                
                if (mainMs > SentisOptimisationsPlugin.Config.PhysicsMsToAlert)
                {
                    _punisher.AlertPlayerGrid(heaviestGrids);
                }
            }
        }

        private static object GetMember(object target, string name)
        {
            var type = target.GetType();
            var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null) return prop.GetValue(target);
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(target);
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
