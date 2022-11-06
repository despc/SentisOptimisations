using System;
using System.Collections.ObjectModel;
using SOPlugin.GUI;
using Torch;

namespace SentisOptimisationsPlugin
{
    public class MainConfig : ViewModel
    {
        
        public MainConfig()
        {
            ConfigShipsInMarket.CollectionChanged += (sender, args) => OnPropertyChanged();
        }
        private double _contractEscortMultiplier = 10;
        private double _contractAcquisitionMultiplier = 30; //Доставка
        private double _contractHaulingtMultiplier = 10; //Перевозка
        private double _contractRepairMultiplier = 10;
        private double _contractFindMultiplier = 25;
        private bool _enabledPcuLimiter = true;
        private bool _enableCheckBeacon = true;
        private bool _enableOnlyEarthSpawn = true;
        private int _maxStaticGridPCU = 200000;
        private int _maxDinamycGridPCU = 30000;
        private int _accelerationToDamage = 1000;
        private int _noDamageFromVoxelsBeforeSpeed = 30;
        private bool _allowMerge = false;
        private bool _includeConnectedGrids = false;
        private bool _adaptiveblockslowdown = false;
        private bool _gasTankOptimisation = true;
        private bool _removeEntityPhantomPatch = false;
        private bool _disableNoOwner = false;
        private String _pathToAsters = "C:\\Asteroids";
        private String _pathToGarage = "D:\\torch-server\\GARAGE";
        private String _planetsWithEco = "Earth,Moon";
        private float _explosivesDamage = 10;
        private String _donations = "";
        private int _contactCountAlert = 150;
        private int _adaptiveBlockSlowdownThreshold = 150;
        private float _shipSuperWelderRadius = 150;
        
        private float _pullItemsSlowdown = 1;
        
        private float _assemblerPullItemsSlowdown = 1;
        
        private float _findProjectedBlocksSlowdown = 1;
        
        private float _projectileAmmoExplosionMultiplier = 0.1f;
        private float _missileAmmoExplosionMultiplier = 0.3f;
        private float _ammoExplosionRadius = 15f;

        private float _physicsMsToAlert = 1.5f;
        private float _physicsMsToPunish = 2f;
        private float _warheadDamageMultiplier = 2.5f;
        private float _physicsMsToPunishImmediately = 5f;
        private int _physicsChecksBeforePunish = 5;
        
        private int _minimumMassForKineticDamage = 5000;
        
        private bool _safeZoneSubGridOptimisation = true;
        private bool _enableRammingForStatic = true;
        private bool _safeZoneWeldOptimisation = false;
        private bool _conveyorCacheEnabled = false;
        private bool _disableLightnings = true;
        private bool _streamingWithoutZip = true;
        private int _safeZonePhysicsThreshold = 10;
        
        //WelderFuck
        private int _coolingSpeed = 30000;
        private int _maxHeat = 2000000;
        private int _weldersOverheatThreshold = 1000000;
        private int _weldersMessageTime = 325;
        
        private bool _welderTweaksEnabled = true;
        private bool _welderNoLimitsCheck = true;
        private bool _welderWeldProjectionsNextFrame = false;
        private bool _welderWeldNextFrames = false;
        private bool _welderCanWeldProjectionsIfWeldedOtherBlocks = false;
        private bool _welderSelfWelding = true;
        private bool _welderExcludeNanobot = true;
        private bool _welderFasterSearch = true;
        private bool _asyncWeld = true;
        private bool _welderSkipCreativeWelding = true;
        
        private bool _asyncExplosion = true;
        
        //Physics
        private float _idealClusterSize = 10000;
        private float _maximumClusterSize = 15000;
        
        //Arrakis
        private String _engineSubtypeKey = "";
        private float _engineMultiplier = 5;
        
        private ObservableCollection<ConfigShipInMarket> configShipsInMarket = new ObservableCollection<ConfigShipInMarket>();

        public ObservableCollection<ConfigShipInMarket> ConfigShipsInMarket

        {
            get { return configShipsInMarket; }
            set
            {
                configShipsInMarket.Clear();
                foreach (ConfigShipInMarket shipInMarket in value)
                {
                    configShipsInMarket.Add(shipInMarket);
                }
            }
        }
        
        [DisplayTab(Name = "Engine Subtype Key", GroupName = "Arrakis", Tab = "Arrakis", Order = 0, Description = "Engine Subtype Key")]
        public String EngineSubtypeKey { get => _engineSubtypeKey; set => SetValue(ref _engineSubtypeKey, value); }
        
        [DisplayTab(Name = "Engine Multiplier", GroupName = "Arrakis", Tab = "Arrakis", Order = 0, Description = "Engine Multiplier")]
        public float EngineMultiplier { get => _engineMultiplier; set => SetValue(ref _engineMultiplier, value); }
        
        [DisplayTab(Name = "Ideal Cluster Size", GroupName = "Physics", Tab = "Physics", Order = 0, Description = "Ideal Cluster Size")]
        public float IdealClusterSize { get => _idealClusterSize; set => SetValue(ref _idealClusterSize, value); }
        
        [DisplayTab(Name = "Maximum Cluster Size", GroupName = "Physics", Tab = "Physics", Order = 0, Description = "Maximum Cluster Size")]
        public float MaximumClusterSize { get => _maximumClusterSize; set => SetValue(ref _maximumClusterSize, value); }
        
        
        [DisplayTab(Name = "Donations list", GroupName = "Donations", Tab = "Donations", Order = 0, Description = "Donations list")]
        public string Donations
        {
            get => _donations;
            set => SetValue(ref _donations, value);
        }
        
        [DisplayTab(Name = "Explosives damage", GroupName = "Balance", Tab = "Balance", Order = 0, Description = "Explosives damage")]
        public float ExplosivesDamage { get => _explosivesDamage; set => SetValue(ref _explosivesDamage, value); }
        
        [DisplayTab(Name = "Projectile Ammo Explosion Multiplier", GroupName = "Balance", Tab = "Balance", Order = 0, Description = "Projectile Ammo Explosion Multiplier")]
        public float ProjectileAmmoExplosionMultiplier { get => _projectileAmmoExplosionMultiplier; set => SetValue(ref _projectileAmmoExplosionMultiplier, value); }
        
        [DisplayTab(Name = "Missile Ammo Explosion Multiplier", GroupName = "Balance", Tab = "Balance", Order = 0, Description = "Missile Ammo Explosion Multiplier")]
        public float MissileAmmoExplosionMultiplier { get => _missileAmmoExplosionMultiplier; set => SetValue(ref _missileAmmoExplosionMultiplier, value); }
        [DisplayTab(Name = "Ammo Explosion Radius", GroupName = "Balance", Tab = "Balance", Order = 0, Description = "Ammo Explosion Radius")]
        public float AmmoExplosionRadius { get => _ammoExplosionRadius; set => SetValue(ref _ammoExplosionRadius, value); }
        
        [DisplayTab(Name = "Warhead damage multiplier", GroupName = "Balance", Tab = "Balance", Order = 0, Description = "Warhead damage multiplier")]
        public float WarheadDamageMultiplier { get => _warheadDamageMultiplier; set => SetValue(ref _warheadDamageMultiplier, value); }
        
        [DisplayTab(Name = "Acceleration to Damage", GroupName = "Balance", Tab = "Balance", Order = 0, Description = "Acceleration to Damage")]
        public int AccelerationToDamage { get => _accelerationToDamage; set => SetValue(ref _accelerationToDamage, value); }
        
        [DisplayTab(Name = "Welders Cooling Speed", GroupName = "Balance", Tab = "Balance", Order = 0, Description = "Welders Cooling Speed")]
        public int CoolingSpeed { get => _coolingSpeed; set => SetValue(ref _coolingSpeed, value); }
        
        [DisplayTab(Name = "Welders Max Heat", GroupName = "Balance", Tab = "Balance", Order = 0, Description = "Welders Max Heat")]
        public int MaxHeat { get => _maxHeat; set => SetValue(ref _maxHeat, value); }
        
        [DisplayTab(Name = "Welders Overheat Threshold", GroupName = "Balance", Tab = "Balance", Order = 0, Description = "Welders Overheat Threshold")]
        public int WeldersOverheatThreshold { get => _weldersOverheatThreshold; set => SetValue(ref _weldersOverheatThreshold, value); }
        
        [DisplayTab(Name = "Welder Message time in ms", GroupName = "Balance", Tab = "Balance", Order = 0, Description = "Welder Message time in ms")]
        public int WeldersMessageTime { get => _weldersMessageTime; set => SetValue(ref _weldersMessageTime, value); }
        
        [DisplayTab(Name = "Path to asteroids", GroupName = "Asteroids", Tab = "Asteroids", Order = 0, Description = "Path to asteroids to restore")]
        public String PathToAsters { get => _pathToAsters; set => SetValue(ref _pathToAsters, value); }
        
        [DisplayTab(Name = "Planets With Economic", GroupName = "Contracts", Tab = "Contracts", Order = 0, Description = "Planets With Economic")]
        public String PlanetsWithEco { get => _planetsWithEco; set => SetValue(ref _planetsWithEco, value); }

        [DisplayTab(Name = "Path to Garage", GroupName = "Garage", Tab = "Garage", Order = 0, Description = "Path to Garage")]
        public String PathToGarage { get => _pathToGarage; set => SetValue(ref _pathToGarage, value); }
        
        [DisplayTab(Name = "Contract escort multiplier", GroupName = "Contracts", Tab = "Contracts", Order = 0, Description = "Contract escort reward multiplier")]
        public double ContractEscortMultiplier
        {
            get => _contractEscortMultiplier;
            set => SetValue(ref _contractEscortMultiplier, value);
        }
        [DisplayTab(Name = "Contract acquisition multiplier", GroupName = "Contracts", Tab = "Contracts", Order = 0, Description = "Contract acquisition reward multiplier")]
        public double ContractAcquisitionMultiplier
        {
            get => _contractAcquisitionMultiplier;
            set => SetValue(ref _contractAcquisitionMultiplier, value);
        }
        [DisplayTab(Name = "Contract hauling multiplier", GroupName = "Contracts", Tab = "Contracts", Order = 0, Description = "Contract hauling reward multiplier")]
        public double ContractHaulingtMultiplier
        {
            get => _contractHaulingtMultiplier;
            set => SetValue(ref _contractHaulingtMultiplier, value);
        }
        [DisplayTab(Name = "Contract repair multiplier", GroupName = "Contracts", Tab = "Contracts", Order = 0, Description = "Contract repair reward multiplier")]
        public double ContractRepairMultiplier
        {
            get => _contractRepairMultiplier;
            set => SetValue(ref _contractRepairMultiplier, value);
        }
        [DisplayTab(Name = "Contract find multiplier", GroupName = "Contracts", Tab = "Contracts", Order = 0, Description = "Contract find reward multiplier")]
        public double ContractFindMultiplier
        {
            get => _contractFindMultiplier;
            set => SetValue(ref _contractFindMultiplier, value);
        }

        [DisplayTab(Name = "Enabled PCU Limiter", GroupName = "PCU limiter", Tab = "PCU limiter", Order = 0, Description = "Enable PCU Limiter")]
        public bool EnabledPcuLimiter
        {
            get => _enabledPcuLimiter;
            set => SetValue(ref _enabledPcuLimiter, value);
        }
        
        [DisplayTab(Name = "Enabled check Beacon", GroupName = "PCU limiter", Tab = "PCU limiter", Order = 0, Description = "Enabled check Beacon")]
        public bool EnableCheckBeacon
        {
            get => _enableCheckBeacon;
            set => SetValue(ref _enableCheckBeacon, value);
        }
        
        [DisplayTab(Name = "Enabled Only Earth Spawn", GroupName = "PCU limiter", Tab = "PCU limiter", Order = 0, Description = "Enabled Only Earth Spawn")]
        public bool EnableOnlyEarthSpawn
        {
            get => _enableOnlyEarthSpawn;
            set => SetValue(ref _enableOnlyEarthSpawn, value);
        }
        
        [DisplayTab(Name = "Safe zone subgrid optimisation", GroupName = "Safe zone", Tab = "Safe zone", Order = 0, Description = "Safe zone subgrid optimisation")]
        public bool SafeZoneSubGridOptimisation
        {
            get => _safeZoneSubGridOptimisation;
            set => SetValue(ref _safeZoneSubGridOptimisation, value);
        }
        
        [DisplayTab(Name = "Static structures receive damage from ramming", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Static structures receive damage from ramming")]
        public bool StaticRamming
        {
            get => _enableRammingForStatic;
            set => SetValue(ref _enableRammingForStatic, value);
        }
        
        [DisplayTab(Name = "Safe zone weld optimisation", GroupName = "Safe zone", Tab = "Safe zone", Order = 0, Description = "Safe zone weld optimisation")]
        public bool SafeWeldOptimisation
        {
            get => _safeZoneWeldOptimisation;
            set => SetValue(ref _safeZoneWeldOptimisation, value);
        }
        
        [DisplayTab(Name = "Safe zone Physics Threshold", GroupName = "Safe zone", Tab = "Safe zone", Order = 0, Description = "Safe zone Physics Threshold")]
        public int SafeZonePhysicsThreshold
        {
            get => _safeZonePhysicsThreshold;
            set => SetValue(ref _safeZonePhysicsThreshold, value);
        }
        
        [DisplayTab(Name = "Max static grid PCU", GroupName = "PCU limiter", Tab = "PCU limiter", Order = 0, Description = "Max static grid PCU")]
        public int MaxStaticGridPCU
        {
            get => _maxStaticGridPCU;
            set => SetValue(ref _maxStaticGridPCU, value);
        }

        [DisplayTab(Name = "Max dynamic grid PCU", GroupName = "PCU limiter", Tab = "PCU limiter", Order = 0, Description = "Max dynamic grid PCU")]
        public int MaxDinamycGridPCU
        {
            get => _maxDinamycGridPCU;
            set => SetValue(ref _maxDinamycGridPCU, value);
        }
        
        
        [DisplayTab(Name = "Pull Items Slowdown", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Pull Items Slowdown")]
        public float PullItemsSlowdown
        {
            get => _pullItemsSlowdown;
            set => SetValue(ref _pullItemsSlowdown, value);
        }
        
        [DisplayTab(Name = "Assembler Pull Items Slowdown", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Assembler Pull Items Slowdown")]
        public float AssemblerPullItemsSlowdown
        {
            get => _assemblerPullItemsSlowdown;
            set => SetValue(ref _assemblerPullItemsSlowdown, value);
        }
        
        [DisplayTab(Name = "Find Projected Blocks Slowdown", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Find Projected Blocks Slowdown")]
        public float FindProjectedBlocksSlowdown
        {
            get => _findProjectedBlocksSlowdown;
            set => SetValue(ref _findProjectedBlocksSlowdown, value);
        } 

        [DisplayTab(Name = "Physics ms to alert", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Physics ms to alert")]
        public float PhysicsMsToAlert
        {
            get => _physicsMsToAlert;
            set => SetValue(ref _physicsMsToAlert, value);
        }

        [DisplayTab(Name = "Physics ms to punish", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Physics ms to punish")]
        public float PhysicsMsToPunish
        {
            get => _physicsMsToPunish;
            set => SetValue(ref _physicsMsToPunish, value);
        }
        
        [DisplayTab(Name = "Physics ms to punish immediately", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Physics ms to punish immediately")]
        public float PhysicsMsToPunishImmediately
        {
            get => _physicsMsToPunishImmediately;
            set => SetValue(ref _physicsMsToPunishImmediately, value);
        }
        
        [DisplayTab(Name = "Physics checks before punish", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Physics checks before punish")]
        public int PhysicsChecksBeforePunish
        {
            get => _physicsChecksBeforePunish;
            set => SetValue(ref _physicsChecksBeforePunish, value);
        }

        [DisplayTab(Name = "Minimum mass for kinetic damage", GroupName = "Damage Tweaks", Tab = "Damage Tweaks", Order = 0, Description = "Minimum mass for kinetic damage")]
        public int MinimumMassForKineticDamage
        {
            get => _minimumMassForKineticDamage;
            set => SetValue(ref _minimumMassForKineticDamage, value);
        }
        
        [DisplayTab(Name = "No damage from voxels before speed", GroupName = "Damage Tweaks", Tab = "Damage Tweaks", Order = 0, Description = "No damage from voxels before speed")]
        public int NoDamageFromVoxelsBeforeSpeed
        {
            get => _noDamageFromVoxelsBeforeSpeed;
            set => SetValue(ref _noDamageFromVoxelsBeforeSpeed, value);
        }
        
        [DisplayTab(Name = "Contact Count to Alert", GroupName = "Damage Tweaks", Tab = "Damage Tweaks", Order = 0, Description = "Contact Count to Alert")]
        public int ContactCountAlert
        {
            get => _contactCountAlert;
            set => SetValue(ref _contactCountAlert, value);
        }
        
        [DisplayTab(Name = "Ship SUPER welder radius" , GroupName = "Ship tool", Tab = "Ship tool", Order = 0, Description = "Ship SUPER welder radius")]
        public float ShipSuperWelderRadius
        {
            get => _shipSuperWelderRadius;
            set => SetValue(ref _shipSuperWelderRadius, value);
        }

        public bool AllowMerge
        {
            get => _allowMerge;
            set => SetValue(ref _allowMerge, value);
        }

        [DisplayTab(Name = "Include connected grids", GroupName = "PCU limiter", Tab = "PCU limiter", Order = 0, Description = "Include grinds connected with CONNECTOR")]
        public bool IncludeConnectedGrids
        {
            get => _includeConnectedGrids;
            set => SetValue(ref _includeConnectedGrids, value);
        }
        
        [DisplayTab(Name = "Experimental crash fix RemoveEntityPhantom", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Experimental crash fix RemoveEntityPhantom")]
        public bool RemoveEntityPhantomPatch
        {
            get => _removeEntityPhantomPatch;
            set => SetValue(ref _removeEntityPhantomPatch, value);
        }
        
        [DisplayTab(Name = "Disable no owner", GroupName = "Balance", Tab = "Balance", Order = 0, Description = "Disable no owner")]
        public bool DisableNoOwner  
        {
            get => _disableNoOwner;
            set => SetValue(ref _disableNoOwner, value);
        }
        
        [DisplayTab(Name = "Fix freeze by voxel streaming", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Fix freeze by voxel streaming")]
        public bool StreamingWithoutZip
        {
            get => _streamingWithoutZip;
            set => SetValue(ref _streamingWithoutZip, value);
        }
        
                
        [DisplayTab(Name = "Adaptive block slowdown", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Adaptive block slowdown")]
        public bool Adaptiveblockslowdown
        {
            get => _adaptiveblockslowdown;
            set => SetValue(ref _adaptiveblockslowdown, value);
        }
        
        [DisplayTab(Name = "Gas Tank Optimisation", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Gas Tank Optimisation")]
        public bool GasTankOptimisation
        {
            get => _gasTankOptimisation;
            set => SetValue(ref _gasTankOptimisation, value);
        }
        
        [DisplayTab(Name = "Adaptive block slowdown threshold", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Adaptive block slowdown threshold")]
        public int AdaptiveBlockSlowdownThreshold
        {
            get => _adaptiveBlockSlowdownThreshold;
            set => SetValue(ref _adaptiveBlockSlowdownThreshold, value);
        }
        
        [DisplayTab(Name = "Conveyor cache enabled", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Conveyor cache enabled")]
        public bool ConveyorCacheEnabled
        {
            get => _conveyorCacheEnabled;
            set => SetValue(ref _conveyorCacheEnabled, value);
        }
        
        [DisplayTab(Name = "Disable Lightnings", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Disable Lightnings")]
        public bool DisableLightnings
        {
            get => _disableLightnings;
            set => SetValue(ref _disableLightnings, value);
        }
        
        
         //=================================================================================================

        [DisplayTab(Name = "Enabled", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 0, Description = "If disabled all off theese features not working. Optimization MyCubeGrid-GetBlocksInsideSphere - also highly recommended")]
        public bool WelderTweaksEnabled { get => _welderTweaksEnabled; set => SetValue(ref _welderTweaksEnabled, value); }

        [DisplayTab(Name = "No Limits Check", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 1, Description = "Welder doesn't check limits, which making welding faster (not recommended)")]
        public bool WelderTweaksNoLimitsCheck { get => _welderNoLimitsCheck; set => SetValue(ref _welderNoLimitsCheck, value); }

        [DisplayTab(Name = "Weld Next Frames", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 2, Description = "Welder block search is in 1 frame, but welding in random next 5 frames (will remove high simulation drops because 100 welders were enabled in 1 frame)")]
        public bool WelderTweaksWeldNextFrames { get => _welderWeldNextFrames; set => SetValue(ref _welderWeldNextFrames, value); }

        [DisplayTab(Name = "Weld Projections Next Frame", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 3, Description = "Welder can weld it self")]
        public bool WelderTweaksWeldProjectionsNextFrame { get => _welderWeldProjectionsNextFrame; set => SetValue(ref _welderWeldProjectionsNextFrame, value); }

        [DisplayTab(Name = "Weld Projections if welded other blocks", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 4, Description = "Welder can weld projections and non projected blocks on same frame (faster welding, less optimization)")]
        public bool WelderTweaksCanWeldProjectionsIfWeldedOtherBlocks { get => _welderCanWeldProjectionsIfWeldedOtherBlocks; set => SetValue(ref _welderCanWeldProjectionsIfWeldedOtherBlocks, value); }

        [DisplayTab(Name = "Self Welding", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 5, Description = "Welder can weld it self")]
        public bool WelderTweaksSelfWelding { get => _welderSelfWelding; set => SetValue(ref _welderSelfWelding, value); }

        [DisplayTab(Name = "Exclude Nanobot", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 6, Description = "Nanobot can't weld as regular welder (and shouldn't)")]
        public bool WelderTweaksExcludeNanobot { get => _welderExcludeNanobot; set => SetValue(ref _welderExcludeNanobot, value); }

        [DisplayTab(Name = "Faster block search", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 7, Description = "Inaccurate block search")]
        public bool WelderTweaksFasterSearch { get => _welderFasterSearch; set => SetValue(ref _welderFasterSearch, value); }

        [DisplayTab(Name = "Skip creative welding", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 8, Description = "Skip creative welding")]
        public bool WelderSkipCreativeWelding { get => _welderSkipCreativeWelding; set => SetValue(ref _welderSkipCreativeWelding, value); }
        
        [DisplayTab(Name = "Async weld", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 8, Description = "Async weld")]
        public bool AsyncWeld { get => _asyncWeld; set => SetValue(ref _asyncWeld, value); }
        
        
        [DisplayTab(Name = "Async explosion", GroupName = "Performance", Tab = "Performance", Order = 8, Description = "Async explosion")]
        public bool AsyncExplosion { get => _asyncExplosion; set => SetValue(ref _asyncExplosion, value); }
    }
}