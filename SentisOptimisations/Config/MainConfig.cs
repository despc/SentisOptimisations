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
        
        //contracts
        private double _contractEscortMultiplier = 10;
        private double _contractAcquisitionMultiplier = 30; //Доставка
        private double _contractHaulingtMultiplier = 10; //Перевозка
        private double _contractRepairMultiplier = 10;
        private double _contractFindMultiplier = 25;
        
        //PCU limiter
        private bool _enabledPcuLimiter = true;
        private int _maxStaticGridPCU = 200000;
        private int _maxDinamycGridPCU = 30000;
        private bool _includeConnectedGrids = false;
        
        //alerts
        private bool _enableCheckBeacon = true;
        
        //optimisations
        private bool _asyncSync = false;
        private bool _gasTankOptimisation = true;
        private bool _gridSystemOptimisations = true;
        private bool _safeZoneSubGridOptimisation = true;
        private int _safeZonePhysicsThreshold = 10; // детект сварки динамики в сз, автоперевод в статику если грид обрабатывается больше N мс
        
        //welders
        private bool _welderTweaksEnabled = true;
        private bool _welderNoLimitsCheck = true;
        private bool _welderCanWeldProjectionsIfWeldedOtherBlocks = false;
        private bool _welderSelfWelding = true;
        private bool _welderFasterSearch = true;
        private bool _asyncWeld = true;
        private bool _asyncWeldAdvanced = true;
        private bool _welderSkipCreativeWelding = true;
        
        //fixes
        private bool _removeEntityPhantomPatch = false;  //фикс краша из за гонки при входе/выходе из сз нескольких гридов/сабгридов одновременно
        
        
        //NPC
        private String _pathToGrids = "C:\\SE\\Arrakis\\NPC";
        private String _guardiansNpcNames = "MKB_9000.sbc,MKB_9000.sbc";
        private int _guardDistanceSpawn = 300;
        
        //other
        private String _pathToAsters = "C:\\Asteroids";
        private String _pathToGarage = "D:\\torch-server\\GARAGE";
        private String _ignoreCleanupSubtypes = "Cargo";
        private String _overrideModIds = "";
        
        //explosions
        private bool _explosionTweaks = false;
        private bool _asyncExplosion = true;
        private float _warheadDamageMultiplier = 2.5f;
        private int _accelerationToDamage = 1000;  // взрыв боеприпаса или взрывчатки от удара об что-то, указывается ускорение объекта которое приводит к взрыву
        private float _explosivesDamage = 10;
        private float _projectileAmmoExplosionMultiplier = 0.1f;
        private float _missileAmmoExplosionMultiplier = 0.3f;
        private float _ammoExplosionRadius = 15f;
        
        //physics profile антипалочная защита
        private bool _enablePhysicsGuard = false;
        private float _physicsMsToAlert = 1.5f;
        private float _physicsMsToPunish = 2f;
        private float _physicsMsToPunishImmediately = 5f;
        private int _physicsChecksBeforePunish = 5;
        private float _checkInsideVoxel = 0.2f;

        //Tweaks
        private bool _autoRenameGrids = false;
        private bool _enableRammingForStatic = true;
        private bool _autoRestoreFromVoxel = false;
        private bool _disableNoOwner = false;
        private bool _disableTurretUpdate = false;
        private bool _enableOnlyEarthSpawn = false;
        private int _noDamageFromVoxelsBeforeSpeed = 30;
        private bool _noDamageFromVoxelsIfNobodyNear = true;
        private int _minimumMassForKineticDamage = 5000;
        private int _raycastLimit = -1;
        
        //Online Reward
        private bool _onlineRewardEnabled = true;
        private int _onlineRewardEachMinutes = 60;
        private string _onlineReward = "Ingot_Platinum=50;Ingot_Uranium=50";
        private string _onlineRewardMessage = "Спасибо что остаётесь с нами, награда за игру на сервере в вашем инвентаре.";
        
        //PvE Zone
        private bool _pveZoneEnabled = false;
        private bool _pveDamageFromNpc = false;
        private string _pveZonePos = "-73641.29:-623775.25:-1089014.23";
        private int _pveZoneRadius = 100000;
        
        //Slowdown
        private bool _slowdownEnabled = true;
        private bool _fixVoxelFreeze = false;
        
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
        
        [DisplayTab(Name = "Slowdown Enabled", GroupName = "Slowdown", Tab = "Slowdown", Order = 0, Description = "Slowdown Enabled")]
        public bool SlowdownEnabled
        {
            get => _slowdownEnabled;
            set => SetValue(ref _slowdownEnabled, value);
        }
        
        [DisplayTab(Name = "Fix Voxel Freeze Enabled", GroupName = "Slowdown", Tab = "Slowdown", Order = 1, Description = "Fix Voxel Freeze Enabled")]
        public bool FixVoxelFreeze
        {
            get => _fixVoxelFreeze;
            set => SetValue(ref _fixVoxelFreeze, value);
        }

        [DisplayTab(Name = "Online Reward Enabled", GroupName = "Online Reward", Tab = "Online Reward", Order = 0, Description = "Online Reward Enabled")]
        public bool OnlineRewardEnabled
        {
            get => _onlineRewardEnabled;
            set => SetValue(ref _onlineRewardEnabled, value);
        }
        
        [DisplayTab(Name = "PvE zone enabled", GroupName = "PvE Zone", Tab = "PvE Zone", Order = 0, Description = "PvE zone enabled")]
        public bool PvEZoneEnabled
        {
            get => _pveZoneEnabled;
            set => SetValue(ref _pveZoneEnabled, value);
        }
        
        [DisplayTab(Name = "Enable damage from NPC", GroupName = "PvE Zone", Tab = "PvE Zone", Order = 1, Description = "Enable damage from NPC")]
        public bool EnableDamageFromNPC
        {
            get => _pveDamageFromNpc;
            set => SetValue(ref _pveDamageFromNpc, value);
        }
        
        [DisplayTab(Name = "PvE zone position", GroupName = "PvE Zone", Tab = "PvE Zone", Order = 2, Description = "PvE zone position")]
        public String PveZonePos { get => _pveZonePos; set => SetValue(ref _pveZonePos, value); }
        
        [DisplayTab(Name = "PvE Zone Radius", GroupName = "PvE Zone", Tab = "PvE Zone", Order = 3, Description = "PvE Zone Radius")]
        public int PveZoneRadius { get => _pveZoneRadius; set => SetValue(ref _pveZoneRadius, value); }
        
        
        [DisplayTab(Name = "Reward for online", GroupName = "Online Reward", Tab = "Online Reward", Order = 1, Description = "Reward for online")]
        public String OnlineReward { get => _onlineReward; set => SetValue(ref _onlineReward, value); }
        
        [DisplayTab(Name = "Reward for online message", GroupName = "Online Reward", Tab = "Online Reward", Order = 1, Description = "Reward for online message")]
        public String OnlineRewardMessage { get => _onlineRewardMessage; set => SetValue(ref _onlineRewardMessage, value); }
        
        [DisplayTab(Name = "Online reward each N minutes", GroupName = "Online Reward", Tab = "Online Reward", Order = 2, Description = "Online reward each N minutes")]
        public int OnlineRewardEachMinutes { get => _onlineRewardEachMinutes; set => SetValue(ref _onlineRewardEachMinutes, value); }
        
        [DisplayTab(Name = "Async explosion", GroupName = "Explosions", Tab = "Explosions", Order = 8, Description = "Async explosion")]
        public bool AsyncExplosion { get => _asyncExplosion; set => SetValue(ref _asyncExplosion, value); }
        
        [DisplayTab(Name = "Explosion Tweaks Enabled", GroupName = "Explosions", Tab = "Explosions", Order = -1, Description = "Explosion Tweaks Enabled")]
        public bool ExplosionTweaks { get => _explosionTweaks; set => SetValue(ref _explosionTweaks, value); }
        
        [DisplayTab(Name = "Explosives damage", GroupName = "Explosions", Tab = "Explosions", Order = 0, Description = "Explosives damage")]
        public float ExplosivesDamage { get => _explosivesDamage; set => SetValue(ref _explosivesDamage, value); }
        
        [DisplayTab(Name = "Projectile Ammo Explosion Multiplier", GroupName = "Explosions", Tab = "Explosions", Order = 0, Description = "Projectile Ammo Explosion Multiplier")]
        public float ProjectileAmmoExplosionMultiplier { get => _projectileAmmoExplosionMultiplier; set => SetValue(ref _projectileAmmoExplosionMultiplier, value); }
        
        [DisplayTab(Name = "Missile Ammo Explosion Multiplier", GroupName = "Explosions", Tab = "Explosions", Order = 0, Description = "Missile Ammo Explosion Multiplier")]
        public float MissileAmmoExplosionMultiplier { get => _missileAmmoExplosionMultiplier; set => SetValue(ref _missileAmmoExplosionMultiplier, value); }
        [DisplayTab(Name = "Ammo Explosion Radius", GroupName = "Explosions", Tab = "Explosions", Order = 0, Description = "Ammo Explosion Radius")]
        public float AmmoExplosionRadius { get => _ammoExplosionRadius; set => SetValue(ref _ammoExplosionRadius, value); }
        
        [DisplayTab(Name = "Warhead damage multiplier", GroupName = "Explosions", Tab = "Explosions", Order = 0, Description = "Warhead damage multiplier")]
        public float WarheadDamageMultiplier { get => _warheadDamageMultiplier; set => SetValue(ref _warheadDamageMultiplier, value); }
        
        [DisplayTab(Name = "Acceleration to Damage", GroupName = "Explosions", Tab = "Explosions", Order = 0, Description = "Acceleration to Damage")]
        public int AccelerationToDamage { get => _accelerationToDamage; set => SetValue(ref _accelerationToDamage, value); }

        [DisplayTab(Name = "Dont clean with blocks", GroupName = "Other", Tab = "Other", Order = 0, Description = "Dont clean with blocks")]
        public String IgnoreCleanupSubtypes { get => _ignoreCleanupSubtypes; set => SetValue(ref _ignoreCleanupSubtypes, value); }

        [DisplayTab(Name = "Path to Garage", GroupName = "Other", Tab = "Other", Order = 0, Description = "Path to Garage")]
        public String PathToGarage { get => _pathToGarage; set => SetValue(ref _pathToGarage, value); }
        
        
        [DisplayTab(Name = "Path to Grids Blueprints", GroupName = "NPC", Tab = "NPC", Order = 0, Description = "Path to Grids Blueprints")]
        public String PathToGrids { get => _pathToGrids; set => SetValue(ref _pathToGrids, value); }
        
        [DisplayTab(Name = "Guardians NPC names", GroupName = "NPC", Tab = "NPC", Order = 0, Description = "Guardians NPC names")]
        public String GuardiansNpcNames { get => _guardiansNpcNames; set => SetValue(ref _guardiansNpcNames, value); }
        
        [DisplayTab(Name = "Guardians spawn distance", GroupName = "NPC", Tab = "NPC", Order = 0, Description = "Guardians spawn distance")]
        public int GuardDistanceSpawn { get => _guardDistanceSpawn; set => SetValue(ref _guardDistanceSpawn, value); }
        
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
        
        [DisplayTab(Name = "Disable Turret Update", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Disable Turret Update if WeaponCore")]
        public bool DisableTurretUpdate
        {
            get => _disableTurretUpdate;
            set => SetValue(ref _disableTurretUpdate, value);
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

        [DisplayTab(Name = "Physics ms to alert", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Physics ms to alert")]
        public float PhysicsMsToAlert
        {
            get => _physicsMsToAlert;
            set => SetValue(ref _physicsMsToAlert, value);
        }
        
        [DisplayTab(Name = "Check Inside Voxel percent", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Check Inside Voxel percent")]
        public float CheckInsideVoxel
        {
            get => _checkInsideVoxel;
            set => SetValue(ref _checkInsideVoxel, value);
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
        [DisplayTab(Name = "Enable physics guard", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Enable physics guard")]
        public bool EnablePhysicsGuard
        {
            get => _enablePhysicsGuard;
            set => SetValue(ref _enablePhysicsGuard, value);
        }

        [DisplayTab(Name = "Minimum mass for kinetic damage", GroupName = "Tweaks", Tab = "Tweaks", Order = 0, Description = "Minimum mass for kinetic damage")]
        public int MinimumMassForKineticDamage
        {
            get => _minimumMassForKineticDamage;
            set => SetValue(ref _minimumMassForKineticDamage, value);
        }
        
        [DisplayTab(Name = "Raycast Limit", GroupName = "Tweaks", Tab = "Tweaks", Order = 0, Description = "Raycast Limit. Need Restart")]
        public int RaycastLimit
        {
            get => _raycastLimit;
            set => SetValue(ref _raycastLimit, value);
        }
        
        [DisplayTab(Name = "No damage from voxels before speed", GroupName = "Tweaks", Tab = "Tweaks", Order = 0, Description = "No damage from voxels before speed")]
        public int NoDamageFromVoxelsBeforeSpeed
        {
            get => _noDamageFromVoxelsBeforeSpeed;
            set => SetValue(ref _noDamageFromVoxelsBeforeSpeed, value);
        }
        
        [DisplayTab(Name = "No Damage From Voxels If Nobody Near", GroupName = "Tweaks", Tab = "Tweaks", Order = 0, Description = "No Damage From Voxels If Nobody Near")]
        public bool NoDamageFromVoxelsIfNobodyNear
        {
            get => _noDamageFromVoxelsIfNobodyNear;
            set => SetValue(ref _noDamageFromVoxelsIfNobodyNear, value);
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
        
        [DisplayTab(Name = "Disable no owner", GroupName = "Tweaks", Tab = "Tweaks", Order = 0, Description = "Disable no owner")]
        public bool DisableNoOwner  
        {
            get => _disableNoOwner;
            set => SetValue(ref _disableNoOwner, value);
        }

        [DisplayTab(Name = "Auto Restore From Voxel", GroupName = "Tweaks", Tab = "Tweaks", Order = 0, Description = "Grids inside voxels rollback for 15sec")]
        public bool AutoRestoreFromVoxel
        {
            get => _autoRestoreFromVoxel;
            set => SetValue(ref _autoRestoreFromVoxel, value);
        }
        [DisplayTab(Name = "Client Only Mods", GroupName = "Other", Tab = "Other", Order = 0, Description = "Client Only Mods splitted with comma")]
        public String OverrideModIds { get => _overrideModIds; set => SetValue(ref _overrideModIds, value); }
        
        [DisplayTab(Name = "Auto Rename Grids", GroupName = "Tweaks", Tab = "Tweaks", Order = 0, Description = "Auto Rename Grids")]
        public bool AutoRenameGrids
        {
            get => _autoRenameGrids;
            set => SetValue(ref _autoRenameGrids, value);
        }

        [DisplayTab(Name = "Gas Tank Optimisation", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Gas Tank Optimisation")]
        public bool GasTankOptimisation
        {
            get => _gasTankOptimisation;
            set => SetValue(ref _gasTankOptimisation, value);
        }
        
        [DisplayTab(Name = "Grid System Optimisations", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Grid System Optimisations")]
        public bool GridSystemOptimisations
        {
            get => _gridSystemOptimisations;
            set => SetValue(ref _gridSystemOptimisations, value);
        } 
        
         //=================================================================================================

        [DisplayTab(Name = "Enabled", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 0, Description = "If disabled all off theese features not working. Optimization MyCubeGrid-GetBlocksInsideSphere - also highly recommended")]
        public bool WelderTweaksEnabled { get => _welderTweaksEnabled; set => SetValue(ref _welderTweaksEnabled, value); }

        [DisplayTab(Name = "No Limits Check", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 1, Description = "Welder doesn't check limits, which making welding faster (not recommended)")]
        public bool WelderTweaksNoLimitsCheck { get => _welderNoLimitsCheck; set => SetValue(ref _welderNoLimitsCheck, value); }

        [DisplayTab(Name = "Weld Projections if welded other blocks", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 4, Description = "Welder can weld projections and non projected blocks on same frame (faster welding, less optimization)")]
        public bool WelderTweaksCanWeldProjectionsIfWeldedOtherBlocks { get => _welderCanWeldProjectionsIfWeldedOtherBlocks; set => SetValue(ref _welderCanWeldProjectionsIfWeldedOtherBlocks, value); }

        [DisplayTab(Name = "Self Welding", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 5, Description = "Welder can weld it self")]
        public bool WelderTweaksSelfWelding { get => _welderSelfWelding; set => SetValue(ref _welderSelfWelding, value); }

        [DisplayTab(Name = "Faster block search", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 7, Description = "Inaccurate block search")]
        public bool WelderTweaksFasterSearch { get => _welderFasterSearch; set => SetValue(ref _welderFasterSearch, value); }

        [DisplayTab(Name = "Skip creative welding", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 8, Description = "Skip creative welding")]
        public bool WelderSkipCreativeWelding { get => _welderSkipCreativeWelding; set => SetValue(ref _welderSkipCreativeWelding, value); }
        
        [DisplayTab(Name = "Async weld", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 8, Description = "Async weld")]
        public bool AsyncWeld { get => _asyncWeld; set => SetValue(ref _asyncWeld, value); }
        
        [DisplayTab(Name = "Async weld Advanced", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 8, Description = "Async weld Advanced")]
        public bool AsyncWeldAdvanced { get => _asyncWeldAdvanced; set => SetValue(ref _asyncWeldAdvanced, value); }
        
    }
}