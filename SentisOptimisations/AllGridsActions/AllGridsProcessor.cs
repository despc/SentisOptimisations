using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using SentisGameplayImprovements.AllGridsActions;
using SentisOptimisations.DelayedLogic;
using SentisOptimisationsPlugin.Freezer;
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin.AllGridsActions
{
    public class AllGridsProcessor
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public CancellationTokenSource CancellationTokenSource { get; set; }
        private FreezeLogic _freezeLogic = new FreezeLogic();

        public void OnLoaded()
        {
            CancellationTokenSource = new CancellationTokenSource();
            Task.Run(CheckLoop);
            Task.Run(FreezerLoop);
        }

        public void OnUnloading()
        {
            CancellationTokenSource.Cancel();
        }

        public async void CheckLoop()
        {
            try
            {
                Log.Info("CheckLoop started");
                while (!CancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(30000);
                        await PhysicsProfilerMonitor.__instance.Profile();
                    }
                    catch (Exception e)
                    {
                        Log.Error("CheckLoop Error", e);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("CheckLoop start Error", e);
            }
        }

        public async void FreezerLoop()
        {
            try
            {
                await Task.Delay(SentisOptimisationsPlugin.Config.DelayBeforeFreezerStartSec * 1000);
                Log.Info("Freezer Loop started");
                while (!CancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(500);
                        var cpuLoad = MySandboxGame.Static.CPULoad;
                        _freezeLogic.UpdateCpuLoad(cpuLoad);
                        var gridsList = new HashSet<IMyCubeGrid>(EntitiesObserver.MyCubeGrids);
                        while (gridsList.Count > 0)
                        {
                            var grid = gridsList.FirstElement();
                            HashSet<IMyCubeGrid> grids = new HashSet<IMyCubeGrid>();
                            MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Physical, grids);
                            grids.ForEach(cubeGrid => gridsList.Remove(cubeGrid));
                            _freezeLogic.CheckGridGroup(grids.Select(cubeGrid => (MyCubeGrid)cubeGrid).ToHashSet());
                        }
                        
                        DelayedProcessor.Instance.AddDelayedAction(DateTime.Now, SentisOptimisationsPlugin.Instance.UpdateGui);
                    }
                    catch (Exception e)
                    {
                        Log.Error("FreezerLoop Error", e);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("FreezerLoop start Error", e);
            }
        }
    }
}