﻿using SentisOptimisationsPlugin.Freezer;
using SOPlugin.GUI;
using Torch;

namespace SentisOptimisationsPlugin
{
    public class MainConfig : ViewModel
    {

        //optimisations
        private bool _gasTankOptimisation = true;
        private bool _gridSystemOptimisations = true;
        private bool _safeZoneSubGridOptimisation = true;
        private int _safeZonePhysicsThreshold = 10; // детект сварки динамики в сз, автоперевод в статику если грид обрабатывается больше N мс
        
        //welders
        private bool _welderTweaksEnabled = true;
        private bool _welderCanWeldProjectionsIfWeldedOtherBlocks = false;
        private bool _welderSelfWelding = true;
        private bool _asyncWeld = true;
        
        //physics profile антипалочная защита
        private bool _enablePhysicsGuard = false;
        private float _physicsMsToAlert = 1.5f;
        private float _physicsMsToPunish = 2f;
        private float _physicsMsToPunishImmediately = 5f;
        private int _physicsChecksBeforePunish = 5;
        private float _checkInsideVoxel = 0.2f;

        //Slowdown
        private bool _slowdownEnabled = true;
        private bool _fixVoxelFreeze = false;
        
        //Freezer
        private bool _freezerEnabled = true;
        private int _freezeDistanceDynamic = 10000;
        private int _freezeDistanceStatic = 3000;
        private bool _freezeNpc = false;
        private bool _freezeSignals = false;
        private bool _enableDebugLogs = true;
        private bool _enableCompensationLogs = true;
        private bool _freezePhysics = false;
        private string _antifreezeBlockSubtypes = "LargeBlockSmallContainer_admin2:LargeBlockSmallContainer_admin";
        private int _minWakeUpIntervalInSec = 600;
        private int _delayBeforeFreezeSec = 5;
        private int _delayBeforeFreezerStartSec = 5;
        
        //Other
        private bool _enableMainDebugLogs = false;
        private int _charSyncDist = 10000;
        
        //Scripts
        private bool _punishHeavyScripts = false;
        private float _scriptMaxTime = 2;
        private int _scriptOvertimeExecTimesBeforePunish = 3;
        
       
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
        
        [DisplayTab(Name = "Safe zone subgrid optimisation", GroupName = "Safe zone", Tab = "Safe zone", Order = 0, Description = "Safe zone subgrid optimisation")]
        public bool SafeZoneSubGridOptimisation
        {
            get => _safeZoneSubGridOptimisation;
            set => SetValue(ref _safeZoneSubGridOptimisation, value);
        }
        
        [DisplayTab(Name = "Safe zone Physics Threshold", GroupName = "Safe zone", Tab = "Safe zone", Order = 0, Description = "Safe zone Physics Threshold")]
        public int SafeZonePhysicsThreshold
        {
            get => _safeZonePhysicsThreshold;
            set => SetValue(ref _safeZonePhysicsThreshold, value);
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

        [DisplayTab(Name = "Enabled", GroupName = "Welder Tweaks", Tab = "Welder Optimizations", Order = 0, Description = "If disabled all off theese features not working. Optimization MyCubeGrid-GetBlocksInsideSphere - also highly recommended")]
        public bool WelderTweaksEnabled { get => _welderTweaksEnabled; set => SetValue(ref _welderTweaksEnabled, value); }

        [DisplayTab(Name = "Weld Projections if welded other blocks", GroupName = "Welder Tweaks", Tab = "Welder Optimizations", Order = 4, Description = "Welder can weld projections and non projected blocks on same frame (faster welding, less optimization)")]
        public bool WelderTweaksCanWeldProjectionsIfWeldedOtherBlocks { get => _welderCanWeldProjectionsIfWeldedOtherBlocks; set => SetValue(ref _welderCanWeldProjectionsIfWeldedOtherBlocks, value); }

        [DisplayTab(Name = "Self Welding", GroupName = "Welder Tweaks", Tab = "Welder Optimizations", Order = 5, Description = "Welder can weld it self")]
        public bool WelderTweaksSelfWelding { get => _welderSelfWelding; set => SetValue(ref _welderSelfWelding, value); }
        
        [DisplayTab(Name = "Async weld", GroupName = "Welder Tweaks", Tab = "Welder Optimizations", Order = 8, Description = "Async weld")]
        public bool AsyncWeld { get => _asyncWeld; set => SetValue(ref _asyncWeld, value); }
        ///Freezer
        [DisplayTab(Name = "Enable Freezer", GroupName = "Freezer", Tab = "Freezer", Order = 0, Description = "Enable Freezer")]
        public bool FreezerEnabled { get => _freezerEnabled; set => SetValue(ref _freezerEnabled, value); }
        
        [DisplayTab(Name = "Freeze distance dynamic", GroupName = "Freezer", Tab = "Freezer", Order = 1, Description = "Freeze distance dynamic")]
        public int FreezeDistanceDynamic { get => _freezeDistanceDynamic; set => SetValue(ref _freezeDistanceDynamic, value); }
        
        [DisplayTab(Name = "Freeze distance static", GroupName = "Freezer", Tab = "Freezer", Order = 1, Description = "Freeze distance static")]
        public int FreezeDistanceStatic { get => _freezeDistanceStatic; set => SetValue(ref _freezeDistanceStatic, value); }
        
        [DisplayTab(Name = "Freeze NPC", GroupName = "Freezer", Tab = "Freezer", Order = 2, Description = "Freeze NPC")]
        public bool FreezeNpc { get => _freezeNpc; set => SetValue(ref _freezeNpc, value); }
        
        [DisplayTab(Name = "Freeze Signals", GroupName = "Freezer", Tab = "Freezer", Order = 3, Description = "Freeze Signals")]
        public bool FreezeSignals { get => _freezeSignals; set => SetValue(ref _freezeSignals, value); }
        
        [DisplayTab(Name = "Antifreeze blocks subtypes", GroupName = "Freezer", Tab = "Freezer", Order = 4, Description = "Antifreeze blocks subtypes")]
        public string AntifreezeBlocksSubtypes { get => _antifreezeBlockSubtypes; set => SetValue(ref _antifreezeBlockSubtypes, value); }
        
        [DisplayTab(Name = "Min WakeUp Interval In Sec", GroupName = "Freezer", Tab = "Freezer", Order = 5, Description = "Min WakeUp Interval In Sec")]
        public int MinWakeUpIntervalInSec { get => _minWakeUpIntervalInSec; set => SetValue(ref _minWakeUpIntervalInSec, value); }
        
        [DisplayTab(Name = "Debug logs", GroupName = "Freezer", Tab = "Freezer", Order = 9, Description = "Debug logs")]
        public bool EnableDebugLogs { get => _enableDebugLogs; set => SetValue(ref _enableDebugLogs, value); }
        
        [DisplayTab(Name = "Delay before freeze in sec", GroupName = "Freezer", Tab = "Freezer", Order = 8, Description = "Delay before freeze in sec")]
        public int DelayBeforeFreezeSec { get => _delayBeforeFreezeSec; set => SetValue(ref _delayBeforeFreezeSec, value); }
        
        [DisplayTab(Name = "Delay before freezer start in sec", GroupName = "Freezer", Tab = "Freezer", Order = 7, Description = "Delay before freezer start in sec")]
        public int DelayBeforeFreezerStartSec { get => _delayBeforeFreezerStartSec; set => SetValue(ref _delayBeforeFreezerStartSec, value); }

        [DisplayTab(Name = "Compensation logs", GroupName = "Freezer", Tab = "Freezer", Order = 10, Description = "Compensation logs")]
        public bool EnableCompensationLogs { get => _enableCompensationLogs; set => SetValue(ref _enableCompensationLogs, value); }
        [DisplayTab(Name = "Freeze Physics", GroupName = "Freezer", Tab = "Freezer", Order = 6, Description = "Freeze Physics")]
        public bool FreezePhysics
        {
            get => _freezePhysics;
            set
            {
                SetValue(ref _freezePhysics, value);
                FreezeLogic.UpdateFreezePhysics(value);
            }
        }
        
        
        [DisplayTab(Name = "Enable debug logs", GroupName = "Other", Tab = "Other", Order = 9, Description = "Enable debug logs")]
        public bool EnableMainDebugLogs { get => _enableMainDebugLogs; set => SetValue(ref _enableMainDebugLogs, value); }
        
        //Scripts
        [DisplayTab(Name = "Enable scripts punish", GroupName = "Scripts", Tab = "Scripts", Order = 0, Description = "Enable Scripts Punish")]
        public bool EnableScriptsPunish { get => _punishHeavyScripts; set => SetValue(ref _punishHeavyScripts, value); }
        
        [DisplayTab(Name = "Scripts max exec time", GroupName = "Scripts", Tab = "Scripts", Order = 1, Description = "Scripts max exec time")]
        public float ScriptsMaxExecTime
        {
            get => _scriptMaxTime;
            set => SetValue(ref _scriptMaxTime, value);
        }
        
        [DisplayTab(Name = "Scripts overtime exec times before punish", GroupName = "Scripts", Tab = "Scripts", Order = 2, Description = "Scripts overtime exec times before punish")]
        public int ScriptOvertimeExecTimesBeforePunish
        {
            get => _scriptOvertimeExecTimesBeforePunish;
            set => SetValue(ref _scriptOvertimeExecTimesBeforePunish, value);
        }
        
        
        [DisplayTab(Name = "Players sync distance", GroupName = "Other", Tab = "Other", Order = 2, Description = "Players sync distance")]
        public int PlayersSyncDistance
        {
            get => _charSyncDist;
            set => SetValue(ref _charSyncDist, value);
        }
    }
}