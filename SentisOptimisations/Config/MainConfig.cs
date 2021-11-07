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
            ConfigAnomalyZone.CollectionChanged += (sender, args) => OnPropertyChanged();
            ConfigShipsInMarket.CollectionChanged += (sender, args) => OnPropertyChanged();
        }
        private double _contractEscortMultiplier = 10;
        private double _contractAcquisitionMultiplier = 30; //Доставка
        private double _contractHaulingtMultiplier = 10; //Перевозка
        private double _contractRepairMultiplier = 10;
        private double _contractFindMultiplier = 25;
        private float _clusterRadius = 16000;
        private bool _enabledPcuLimiter = true;
        private int _maxStaticGridPCU = 200000;
        private int _azMessageTime = 960;
        private int _maxDinamycGridPCU = 30000;
        private int _noDamageFromVoxelsBeforeSpeed = 30;
        private int _cluster1BuildDelay = 30;
        private int _cluster10BuildDelay = 250;
        private int _cluster100BuildDelay = 1000;
        private int _oldGridProcessorDays = 10;
        private bool _allowProjection = true;
        private bool _azPointsForOnlineEnemies = false;
        private bool _clustersEnabled = false;
        private bool _allowMerge = false;
        private bool _includeConnectedGrids = false;
        private bool _removeEntityPhantomPatch = false;
        private String _pathToAsters = "C:\\Asteroids";
        private bool _garageEnabled = true;
        private String _pathToGarage = "D:\\torch-server\\GARAGE";
        private long _azOwner = 144115188075855912;
        private String _azReward = "PhysicalObject_SpaceCredit=120000;Component_ZoneChip=1";
        private String _azWinners = "";
        private int _azPointsRemovedOnDeath = 1;
        private int _azPointsAddOnCaptured = 1;
        private int _azProgressWhenComplete = 300;
        private int _azMinLargeGridBlockCount = 300;
        private int _azMinSmallGridBlockCount = 300;
        
        private float _shipDrillRadiusMultiplier = 2;
        private float _shipGrinderWelderRadiusMultiplier = 2;
        private float _shipWelderRadius = 8;
        private float _shipSuperWelderRadius = 150;
        
        private float _pullItemsSlowdown = 1;
        
        private float _assemblerPullItemsSlowdown = 1;
        
        private float _findProjectedBlocksSlowdown = 1;
        
        private float _thrustPowerMultiplier = 10f;
        private float _missileInitialSpeed = 500f;
        private float _missileAcceleration = 0f;
        private float _missileDamage = 1500f;
        private float _turretsDamageMultiplier = 0.1f;
        private float _physicsMsToAlert = 1.5f;
        private float _physicsMsToPunish = 2f;
        private float _physicsMsToPunishImmediately = 5f;
        private int _physicsChecksBeforePunish = 5;
        
        private int _minimumMassForKineticDamage = 5000;
        
        private bool _safeZoneSubGridOptimisation = true;
        
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
        
        private ObservableCollection<ConfigAnomalyZone> configAnomalyZone = new ObservableCollection<ConfigAnomalyZone>();

        public ObservableCollection<ConfigAnomalyZone> ConfigAnomalyZone

        {
            get { return configAnomalyZone; }
            set
            {
                configShipsInMarket.Clear();
                foreach (ConfigAnomalyZone shipInMarket in value)
                {
                    configAnomalyZone.Add(shipInMarket);
                }
            }
        }
        
        [DisplayTab(Name = "Path to asteroids", GroupName = "Asteroids", Tab = "Asteroids", Order = 0, Description = "Path to asteroids to restore")]
        public String PathToAsters { get => _pathToAsters; set => SetValue(ref _pathToAsters, value); }
        
        [DisplayTab(Name = "Garage enabled", GroupName = "Garage", Tab = "Garage", Order = 0, Description = "Garage enabled")]
        public bool GarageEnabled
        {
            get => _garageEnabled;
            set => SetValue(ref _garageEnabled, value);
        }
        
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
        
        [DisplayTab(Name = "Safe zone subgrid optimisation", GroupName = "Safe zone", Tab = "Safe zone", Order = 0, Description = "Safe zone subgrid optimisation")]
        public bool SafeZoneSubGridOptimisation
        {
            get => _safeZoneSubGridOptimisation;
            set => SetValue(ref _safeZoneSubGridOptimisation, value);
        }
        
        [DisplayTab(Name = "Max static grid PCU", GroupName = "PCU limiter", Tab = "PCU limiter", Order = 0, Description = "Max static grid PCU")]
        public int MaxStaticGridPCU
        {
            get => _maxStaticGridPCU;
            set => SetValue(ref _maxStaticGridPCU, value);
        }
        
        [DisplayTab(Name = "Move grids to garage after player offline days", GroupName = "Garage", Tab = "Garage", Order = 0, Description = "Move grids to garage after player offline days")]
        public int OldGridProcessorDays
        {
            get => _oldGridProcessorDays;
            set => SetValue(ref _oldGridProcessorDays, value);
        }
        
        [DisplayTab(Name = "Anomaly Zone Message Time", GroupName = "Anomaly Zone", Tab = "Anomaly Zone", Order = 0, Description = "Anomaly Zone Message Time")]
        public int AzMessageTime
        {
            get => _azMessageTime;
            set => SetValue(ref _azMessageTime, value);
        }
        
        [DisplayTab(Name = "Anomaly Zone week winners", GroupName = "Anomaly Zone", Tab = "Anomaly Zone", Order = 0, Description = "Anomaly Zone week winners")]
        public String AzWinners { get => _azWinners; set => SetValue(ref _azWinners, value); }
        
        [DisplayTab(Name = "Zone owner", GroupName = "Anomaly Zone", Tab = "Anomaly Zone", Order = 0, Description = "Anomaly Zone owner")]
        public long AzOwner
        {
            get => _azOwner;
            set => SetValue(ref _azOwner, value);
        }
        
        [DisplayTab(Name = "Zone reward", GroupName = "Anomaly Zone", Tab = "Anomaly Zone", Order = 0, Description = "Zone reward")]
        public string AzReward
        {
            get => _azReward;
            set => SetValue(ref _azReward, value);
        }
        
        [DisplayTab(Name = "Progress when complete", GroupName = "Anomaly Zone", Tab = "Anomaly Zone", Order = 0, Description = "Progress when complete")]
        public int AzProgressWhenComplete
        {
            get => _azProgressWhenComplete;
            set => SetValue(ref _azProgressWhenComplete, value);
        }
                
        [DisplayTab(Name = "Min large grid block count", GroupName = "Anomaly Zone", Tab = "Anomaly Zone", Order = 0, Description = "Min large grid block count")]
        public int AzMinLargeGridBlockCount
        {
            get => _azMinLargeGridBlockCount;
            set => SetValue(ref _azMinLargeGridBlockCount, value);
        }
        
        [DisplayTab(Name = "Min small grid block count", GroupName = "Anomaly Zone", Tab = "Anomaly Zone", Order = 0, Description = "Min small grid block count")]
        public int AzMinSmallGridBlockCount
        {
            get => _azMinSmallGridBlockCount;
            set => SetValue(ref _azMinSmallGridBlockCount, value);
        }
        
        [DisplayTab(Name = "Points removed on death", GroupName = "Anomaly Zone", Tab = "Anomaly Zone", Order = 0, Description = "Points removed on death")]
        public int AzPointsRemovedOnDeath
        {
            get => _azPointsRemovedOnDeath;
            set => SetValue(ref _azPointsRemovedOnDeath, value);
        }
        
        [DisplayTab(Name = "Points reward", GroupName = "Anomaly Zone", Tab = "Anomaly Zone", Order = 0, Description = "Points reward")]
        public int AzPointsAddOnCaptured
        {
            get => _azPointsAddOnCaptured;
            set => SetValue(ref _azPointsAddOnCaptured, value);
        }
        
        
        [DisplayTab(Name = "Max dynamic grid PCU", GroupName = "PCU limiter", Tab = "PCU limiter", Order = 0, Description = "Max dynamic grid PCU")]
        public int MaxDinamycGridPCU
        {
            get => _maxDinamycGridPCU;
            set => SetValue(ref _maxDinamycGridPCU, value);
        }
        
        
        [DisplayTab(Name = "Cluster radius", GroupName = "Clusters", Tab = "Clusters", Order = 0, Description = "Cluster radius")]
        public float ClusterRadius
        {
            get => _clusterRadius;
            set => SetValue(ref _clusterRadius, value);
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
        
        [DisplayTab(Name = "Thruster magnitude multiplier", GroupName = "Balance", Tab = "Balance", Order = 0, Description = "Thruster magnitude multiplier")]
        public float ThrustPowerMultiplier
        {
            get => _thrustPowerMultiplier;
            set => SetValue(ref _thrustPowerMultiplier, value);
        }
        
        [DisplayTab(Name = "Missile initial Speed", GroupName = "Balance", Tab = "Balance", Order = 0, Description = "Missile initial Speed")]
        public float MissileInitialSpeed
        {
            get => _missileInitialSpeed;
            set => SetValue(ref _missileInitialSpeed, value);
        }
        
        [DisplayTab(Name = "Missile Acceleration", GroupName = "Balance", Tab = "Balance", Order = 0, Description = "Missile Acceleration")]
        public float MissileAcceleration
        {
            get => _missileAcceleration;
            set => SetValue(ref _missileAcceleration, value);
        }
        
        [DisplayTab(Name = "Missile Damage", GroupName = "Balance", Tab = "Balance", Order = 0, Description = "Missile Damage")]
        public float MissileDamage
        {
            get => _missileDamage;
            set => SetValue(ref _missileDamage, value);
        }
        
        [DisplayTab(Name = "Damage to turrets multiplier", GroupName = "Balance", Tab = "Balance", Order = 0, Description = "Damage to turrets multiplier")]
        public float TurretsDamageMultiplier
        {
            get => _turretsDamageMultiplier;
            set => SetValue(ref _turretsDamageMultiplier, value);
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
        
        [DisplayTab(Name = "Cluster1 build delay", GroupName = "Clusters", Tab = "Clusters", Order = 0, Description = "Cluster1 build delay")]
        public int Cluster1BuildDelay
        {
            get => _cluster1BuildDelay;
            set => SetValue(ref _cluster1BuildDelay, value);
        }
        
        [DisplayTab(Name = "Cluster10 build delay", GroupName = "Clusters", Tab = "Clusters", Order = 0, Description = "Cluster10 build delay")]
        public int Cluster10BuildDelay
        {
            get => _cluster10BuildDelay;
            set => SetValue(ref _cluster10BuildDelay, value);
        }
        
        [DisplayTab(Name = "Cluster100 build delay", GroupName = "Clusters", Tab = "Clusters", Order = 0, Description = "Cluster100 build delay")]
        public int Cluster100BuildDelay
        {
            get => _cluster100BuildDelay;
            set => SetValue(ref _cluster100BuildDelay, value);
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
        
        [DisplayTab(Name = "Ship drill radius multiplier", GroupName = "Ship tool", Tab = "Ship tool", Order = 0, Description = "Ship drill radius multiplier")]
        public float ShipDrillRadiusMultiplier
        {
            get => _shipDrillRadiusMultiplier;
            set => SetValue(ref _shipDrillRadiusMultiplier, value);
        }
        
        [DisplayTab(Name = "Ship grinder/welder radius multiplier", GroupName = "Ship tool", Tab = "Ship tool", Order = 0, Description = "Ship grinder/welder radius multiplier")]
        public float ShipGrinderWelderRadiusMultiplier
        {
            get => _shipGrinderWelderRadiusMultiplier;
            set => SetValue(ref _shipGrinderWelderRadiusMultiplier, value);
        }
        
        [DisplayTab(Name = "Ship welder radius" , GroupName = "Ship tool", Tab = "Ship tool", Order = 0, Description = "Ship welder radius")]
        public float ShipWelderRadius
        {
            get => _shipWelderRadius;
            set => SetValue(ref _shipWelderRadius, value);
        }
        
        [DisplayTab(Name = "Ship SUPER welder radius" , GroupName = "Ship tool", Tab = "Ship tool", Order = 0, Description = "Ship SUPER welder radius")]
        public float ShipSuperWelderRadius
        {
            get => _shipSuperWelderRadius;
            set => SetValue(ref _shipSuperWelderRadius, value);
        }
        
        public bool AllowProjection
        {
            get => _allowProjection;
            set => SetValue(ref _allowProjection, value);
        }
        [DisplayTab(Name = "Clusters enabled", GroupName = "Clusters", Tab = "Clusters", Order = 0, Description = "Clusters enabled")]
        public bool ClustersEnabled
        {
            get => _clustersEnabled;
            set => SetValue(ref _clustersEnabled, value);
        }
        
        [DisplayTab(Name = "Points by online enemies", GroupName = "Anomaly Zone", Tab = "Anomaly Zone", Order = 0, Description = "Points by online enemies")]
        public bool AzPointsForOnlineEnemies
        {
            get => _azPointsForOnlineEnemies;
            set => SetValue(ref _azPointsForOnlineEnemies, value);
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
    }
}