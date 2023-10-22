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
        private bool _welderNoLimitsCheck = true;
        private bool _welderCanWeldProjectionsIfWeldedOtherBlocks = false;
        private bool _welderSelfWelding = true;
        private bool _welderFasterSearch = true;
        private bool _asyncWeld = true;
        private bool _asyncWeldAdvanced = true;
        private bool _welderSkipCreativeWelding = true;
        
        //fixes
        private bool _removeEntityPhantomPatch = false;  //фикс краша из за гонки при входе/выходе из сз нескольких гридов/сабгридов одновременно
        
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
        
        [DisplayTab(Name = "Experimental crash fix RemoveEntityPhantom", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Experimental crash fix RemoveEntityPhantom")]
        public bool RemoveEntityPhantomPatch
        {
            get => _removeEntityPhantomPatch;
            set => SetValue(ref _removeEntityPhantomPatch, value);
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

        [DisplayTab(Name = "Skip creative welding", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 8, Description = "Skip creative welding")]
        public bool WelderSkipCreativeWelding { get => _welderSkipCreativeWelding; set => SetValue(ref _welderSkipCreativeWelding, value); }
        
        [DisplayTab(Name = "Async weld", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 8, Description = "Async weld")]
        public bool AsyncWeld { get => _asyncWeld; set => SetValue(ref _asyncWeld, value); }
        
        [DisplayTab(Name = "Async weld Advanced", GroupName = "Welder Tweaks (WIP)", Tab = "Welder Optimizations", Order = 8, Description = "Async weld Advanced")]
        public bool AsyncWeldAdvanced { get => _asyncWeldAdvanced; set => SetValue(ref _asyncWeldAdvanced, value); }
        
    }
}