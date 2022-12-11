using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using TorchMonitor.ProfilerMonitors;
using VRage.ModAPI;

namespace SentisOptimisationsPlugin.AllGridsActions
{
    public class AllGridsObserver
    {
        public static FallInVoxelDetector FallInVoxelDetector = new FallInVoxelDetector();
        private GridAutoRenamer _autoRenamer = new GridAutoRenamer();

        public static HashSet<MyPlanet> Planets = new HashSet<MyPlanet>();
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public CancellationTokenSource CancellationTokenSource { get; set; }

        public void OnLoaded()
        {
            CancellationTokenSource = new CancellationTokenSource();
            CheckLoop();

            HashSet<IMyEntity> list = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(list);
            foreach (var entity in list)
            {
                if (entity == null || !MyAPIGateway.Entities.Exist(entity)) continue;

                var myPlanet = entity as MyPlanet;
                if (myPlanet == null) continue;
                Planets.Add(myPlanet);
            }
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
                        await Task.Delay(20000);
                        var myCubeGrids = MyEntities.GetEntities().OfType<MyCubeGrid>();
                        await Task.Run(() => { CheckAllGrids(myCubeGrids); });
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


        private void CheckAllGrids(IEnumerable<MyCubeGrid> myCubeGrids)
        {
            foreach (var grid in myCubeGrids)
            {
                if (CancellationTokenSource.Token.IsCancellationRequested)
                    break;
                SentisOptimisationsPlugin._limiter.CheckGrid(grid);
                if (SentisOptimisationsPlugin.Config.AutoRestoreFromVoxel)
                {
                    FallInVoxelDetector.CheckAndSavePos(grid);
                }

                if (SentisOptimisationsPlugin.Config.AutoRenameGrids)
                {
                    _autoRenamer.CheckAndRename(grid);
                }

                if (SentisOptimisationsPlugin.Config.DisableNoOwner)
                {
                    CheckNobodyOwner(grid);
                }
            }
        }

        private void CheckNobodyOwner(MyCubeGrid grid)
        {
            foreach (var myCubeBlock in grid.GetFatBlocks())
            {
                if (myCubeBlock.BlockDefinition.OwnershipIntegrityRatio != 0 && myCubeBlock.OwnerId == 0)
                {
                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    {
                        try
                        {
                            if (myCubeBlock is IMyFunctionalBlock)
                            {
                                ((IMyFunctionalBlock)myCubeBlock).Enabled = false;
                            }
                        }
                        catch (Exception e)
                        {
                            Log.Warn("Prevent crash", e);
                        }
                    });
                }
            }
        }
    }
}