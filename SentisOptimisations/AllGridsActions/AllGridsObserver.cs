using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.WorldEnvironment;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin.AllGridsActions
{
    public class AllGridsObserver
    {
        public static FallInVoxelDetector FallInVoxelDetector = new FallInVoxelDetector();
        private GridAutoRenamer _autoRenamer = new GridAutoRenamer();
        private OnlineReward _onlineReward = new OnlineReward();
        private AsteroidReverter _asteroidReverter = new AsteroidReverter();
        private PvEGridChecker _pvEGridChecker = new PvEGridChecker();
        private NpcStationsPowerFix _npcStationsPowerFix = new NpcStationsPowerFix();
        public static HashSet<MyEntity> entitiesToShipTools = new HashSet<MyEntity>();
        public static HashSet<MyCubeGrid> myCubeGrids = new HashSet<MyCubeGrid>();
        public static HashSet<IMyVoxelMap> myVoxelMaps = new HashSet<IMyVoxelMap>();
        public static HashSet<MyPlanet> Planets = new HashSet<MyPlanet>();
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private int counter = 0;
        public void MyEntitiesOnOnEntityRemove(MyEntity entity)
        {
            if (entity is MyEnvironmentSector 
                || entity is MyCubeGrid 
                || entity is MyPlanet 
                || entity is IMyVoxelMap
                || entity is MyCharacter)
            {
                entitiesToShipTools.Remove(entity);
            }
            
            if (entity is MyCubeGrid)
            {
                myCubeGrids.Remove((MyCubeGrid)entity);
                return;
            }
            
            if (entity is MyPlanet)
            {
                Planets.Remove((MyPlanet)entity);
                return;
            }
            if (entity is IMyVoxelMap)
            {
                myVoxelMaps.Remove((IMyVoxelMap)entity);
            }
        }

        public void MyEntitiesOnOnEntityAdd(MyEntity entity)
        {
            if (entity is MyEnvironmentSector 
                || entity is MyCubeGrid 
                || entity is MyPlanet 
                || entity is IMyVoxelMap
                || entity is MyCharacter)
            {
                entitiesToShipTools.Add(entity);
            }
            
            if (entity is MyPlanet)
            {
                Log.Warn("Add planet to list " + entity.DisplayName);
                Planets.Add((MyPlanet)entity);
                return;
            }
            
            if (entity is MyCubeGrid)
            {
                myCubeGrids.Add((MyCubeGrid)entity);
                return;
            }

            if (entity is IMyVoxelMap)
            {
                myVoxelMaps.Add((IMyVoxelMap)entity);
            }
        }
        
        public CancellationTokenSource CancellationTokenSource { get; set; }

        public void OnLoaded()
        {
            CancellationTokenSource = new CancellationTokenSource();
            Task.Run(CheckLoop);
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
                        counter++;
                        await Task.Delay(30000);
                        await Task.Run(() =>
                        {
                            try
                            {
                                _asteroidReverter.CheckAndRestore(new HashSet<IMyVoxelMap>(myVoxelMaps));
                            }
                            catch (Exception e)
                            {
                                Log.Error(e);
                            }
                            
                        });
                        await Task.Run(CheckAllGrids);
                        if (counter % 5 == 0)
                        {
                            await Task.Run(() => _npcStationsPowerFix.RefillPowerStations());
                        }

                        await Task.Run(() =>
                        {
                            try
                            {
                                _onlineReward.RewardOnline();
                            }
                            catch (Exception e)
                            {
                                Log.Error("Async exception " + e);
                            }
                        });
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

        private void CheckAllGrids()
        {
            try
            {
                foreach (var grid in new HashSet<MyCubeGrid>(myCubeGrids))
                {
                    if (CancellationTokenSource.Token.IsCancellationRequested)
                        break;
                    if (grid == null)
                    {
                        continue;
                    }
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
                    if (SentisOptimisationsPlugin.Config.PvEZoneEnabled)
                    {
                        _pvEGridChecker.CheckGridIsPvE(grid);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
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