using System;
using System.Collections.ObjectModel;
using SOPlugin.GUI;
using Torch;

namespace SentisOptimisationsPlugin
{
    public class MainConfig : ViewModel
    {
        private double _contractEscortMultiplier = 10;
        private double _contractAcquisitionMultiplier = 30; //Доставка
        private double _contractHaulingtMultiplier = 10; //Перевозка
        private double _contractRepairMultiplier = 10;
        private double _contractFindMultiplier = 25;
        private float _clusterRadius = 16000;
        private bool _enabledPcuLimiter = true;
        private int _maxStaticGridPCU = 200000;
        private int _maxDinamycGridPCU = 30000;
        private int _noDamageFromVoxelsBeforeSpeed = 30;
        private int _cluster1BuildDelay = 30;
        private int _cluster10BuildDelay = 250;
        private int _cluster100BuildDelay = 1000;
        private bool _allowProjection = true;
        private bool _clustersEnabled = false;
        private bool _allowMerge = false;
        private bool _includeConnectedGrids = false;
        private String _pathToAsters = "C:\\Asteroids";
        private bool _garageEnabled = true;
        private String _pathToGarage = "D:\\torch-server\\GARAGE";
        
        private float _shipDrillRadiusMultiplier = 2;
        private float _shipGrinderWelderRadiusMultiplier = 2;
        
        private int _minimumMassForKineticDamage = 5000;
        
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
        
        
        [DisplayTab(Name = "Cluster radius", GroupName = "Clusters", Tab = "Clusters", Order = 0, Description = "Cluster radius")]
        public float ClusterRadius
        {
            get => _clusterRadius;
            set => SetValue(ref _clusterRadius, value);
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
    }
}