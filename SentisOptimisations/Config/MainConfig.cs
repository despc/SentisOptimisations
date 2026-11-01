using SentisOptimisationsPlugin.Freezer;
using SOPlugin.GUI;
using Torch;

namespace SentisOptimisationsPlugin
{
    public class MainConfig : ViewModel
    {

        //optimisations
        private bool _gasTankOptimisation = true;
        private bool _safeZoneGridTracking = true;
        private bool _mechanicalTopCheck = true;
        private bool _conveyorReach = true;
        private bool _dockedDampenersCalm = true;
        private bool _dockedCollisionsOff = true;
        
        //welders
        private bool _welderTweaksEnabled = true;
        private bool _welderCanWeldProjectionsIfWeldedOtherBlocks = false;
        private bool _welderSelfWelding = true;
        private int _projectionBuildsPerFrame = 1;
        private int _welderIdleActivationSeconds = 3;
        private int _welderIdleActivationsBeforeSlowdown = 4;

        //physics profile антипалочная защита
        private bool _enablePhysicsGuard = true;
        private bool _disablePhysicsMonitor;
        private float _physicsMsToAlert = 1.5f;
        private float _physicsMsToPunish = 2f;
        private float _physicsMsToPunishImmediately = 5f;
        private int _physicsChecksBeforePunish = 5;
        private float _checkInsideVoxel = 0.2f;

        
        //Freezer
        private bool _freezerEnabled = true;
        private int _freezeDistanceDynamic = 10000;
        private int _freezeDistanceStatic = 3000;
        private bool _freezeNpc = false;
        private bool _freezeSignals = false;
        private bool _enableDebugLogs = false;
        private bool _enableCompensationLogs = false;
        private bool _diagnosticLogs = false;
        private bool _freezePhysics = false;
        private string _antifreezeBlockSubtypes = "LargeBlockSmallContainer_admin2:LargeBlockSmallContainer_admin";
        private int _minWakeUpIntervalInSec = 600;
        private int _delayBeforeFreezeSec = 5;
        private int _delayBeforeFreezerStartSec = 5;
        
        //Other
        private bool _enableMainDebugLogs = false;
        private int _physicsThreads = Optimizer.Optimizations.HavokThreadPool.DefaultThreads;
        private bool _gameThreadOnFastCores = true;
        
        //Scripts
        private bool _punishHeavyScripts = false;
        private float _scriptMaxTime = 2;
        private float _scriptsMaxMsPerFrame = 0.5f;
        private int _scriptOvertimeExecTimesBeforePunish = 3;
        
       
        
        
        
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
        [DisplayTab(Name = "Enable physics guard", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Enable physics guard")]
        public bool EnablePhysicsGuard
        {
            get => _enablePhysicsGuard;
            set => SetValue(ref _enablePhysicsGuard, value);
        }
        
        [DisplayTab(Name = "Выключить мониторинг физики", GroupName = "Performance", Tab = "Performance", Order = 0,
            Description = "The physics step as the game does it: no cluster stepped alone and timed, no contacts counted, no physics guard. To check whether something is from the monitor; applies from the next frame")]
        public bool DisablePhysicsMonitor
        {
            get => _disablePhysicsMonitor;
            set => SetValue(ref _disablePhysicsMonitor, value);
        }

        [DisplayTab(Name = "Gas Tank Optimisation", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Gas Tank Optimisation")]
        public bool GasTankOptimisation
        {
            get => _gasTankOptimisation;
            set => SetValue(ref _gasTankOptimisation, value);
        }

        [DisplayTab(Name = "Safe zone grid tracking", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "Safe zones find dynamic grids by geometry instead of their physics phantom (welding inside a zone stops costing physics time). Applies to zones created or rebuilt after the change, all zones after a restart")]
        public bool SafeZoneGridTracking
        {
            get => _safeZoneGridTracking;
            set => SetValue(ref _safeZoneGridTracking, value);
        }

        [DisplayTab(Name = "Conveyor reach cache", GroupName = "Performance", Tab = "Performance", Order = 0, Description = "\"Can the conveyor bring this item from there to here\" (CanTransferItemTo, IsConnectedTo, transfers through conveyors: the freezer's assemblers, scripts, mods) answered from one search of the network per destination, item and owner instead of a search of the whole network per pair; the answers are vanilla's. Off: vanilla. Takes effect at once")]
        public bool ConveyorReach
        {
            get => _conveyorReach;
            set => SetValue(ref _conveyorReach, value);
        }

        [DisplayTab(Name = "Rotor head check", GroupName = "Fixes", Tab = "Performance", Order = 0, Description = "After a rotor, hinge or advanced rotor is placed or given a head from the terminal, its head is checked for a few seconds: one off its place, tilted, not attached or without its constraint is put back where the stator puts it and attached again, and the clients are sent the connection again. Takes effect at once")]
        public bool MechanicalTopCheck
        {
            get => _mechanicalTopCheck;
            set => SetValue(ref _mechanicalTopCheck, value);
        }

        [DisplayTab(Name = "Docked grids calm", GroupName = "Fixes", Tab = "Performance", Order = 0, Description = "A grid locked by a connector to another dynamic grid does not dampen while the whole locked group stands (under 5 cm/s): its dampeners kicked against the lock every frame and the docked grids shook. Gravity is still held. Takes effect at once")]
        public bool DockedDampenersCalm
        {
            get => _dockedDampenersCalm;
            set => SetValue(ref _dockedDampenersCalm, value);
        }

        [DisplayTab(Name = "Docked grids do not collide", GroupName = "Fixes", Tab = "Performance", Order = 0, Description = "Grids locked by connectors do not collide with each other, as a rotor's head with its stator: where their hulls reached into each other, the lock and the contact fought (shaking) and threw them apart when the lock let go. After parting, the collisions come back once the shapes are clear. Takes effect at once")]
        public bool DockedCollisionsOff
        {
            get => _dockedCollisionsOff;
            set => SetValue(ref _dockedCollisionsOff, value);
        }
        
         //=================================================================================================

        [DisplayTab(Name = "Enabled", GroupName = "Welder Tweaks", Tab = "Welder Optimizations", Order = 0, Description = "If disabled all off theese features not working. Optimization MyCubeGrid-GetBlocksInsideSphere - also highly recommended")]
        public bool WelderTweaksEnabled { get => _welderTweaksEnabled; set => SetValue(ref _welderTweaksEnabled, value); }

        [DisplayTab(Name = "Weld Projections if welded other blocks", GroupName = "Welder Tweaks", Tab = "Welder Optimizations", Order = 4, Description = "Welder can weld projections and non projected blocks on same frame (faster welding, less optimization)")]
        public bool WelderTweaksCanWeldProjectionsIfWeldedOtherBlocks { get => _welderCanWeldProjectionsIfWeldedOtherBlocks; set => SetValue(ref _welderCanWeldProjectionsIfWeldedOtherBlocks, value); }

        [DisplayTab(Name = "Self Welding", GroupName = "Welder Tweaks", Tab = "Welder Optimizations", Order = 5, Description = "Welder can weld it self")]
        public bool WelderTweaksSelfWelding { get => _welderSelfWelding; set => SetValue(ref _welderSelfWelding, value); }

        [DisplayTab(Name = "Projection builds per frame per player", GroupName = "Welder Tweaks", Tab = "Welder Optimizations", Order = 6, Description = "How many projected blocks one player's welders (the welder's owner) materialize per simulation frame. Minimum 1; runtime adjustable.")]
        public int ProjectionBuildsPerFrame
        {
            get => _projectionBuildsPerFrame;
            set => SetValue(ref _projectionBuildsPerFrame, value < 1 ? 1 : value);
        }

        [DisplayTab(Name = "Idle activation interval, seconds", GroupName = "Welder Tweaks", Tab = "Welder Optimizations", Order = 8, Description = "An enabled welder in the idle mode activates once per this interval instead of four times a second (sphere query, projection scan). Slow activations are staggered per welder so they do not fire on the same frame. Any real work returns full speed at once. 0 disables the slowdown; runtime adjustable.")]
        public int WelderIdleActivationSeconds
        {
            get => _welderIdleActivationSeconds;
            set => SetValue(ref _welderIdleActivationSeconds, value < 0 ? 0 : value);
        }

        [DisplayTab(Name = "Idle activations before slowdown", GroupName = "Welder Tweaks", Tab = "Welder Optimizations", Order = 9, Description = "How many consecutive activations with nothing to weld put the welder into the slow mode. Any activation with real work (welding or building a projection) resets the counter and leaves the slow mode immediately. Minimum 1; runtime adjustable.")]
        public int WelderIdleActivationsBeforeSlowdown
        {
            get => _welderIdleActivationsBeforeSlowdown;
            set => SetValue(ref _welderIdleActivationsBeforeSlowdown, value < 1 ? 1 : value);
        }

        // Shown, not saved: no setter, so the config file never holds it; the settings page reads it anew every second.
        [DisplayTab(Name = "Welders waiting to build a projection block", GroupName = "Welder Tweaks", Tab = "Welder Optimizations", Order = 7, Description = "Live, by player, the most at once in the last second: welders that found no room in their player's projection build budget for the frame and wait for the next frame with room; each builds at most one block when its turn comes. Only the players who have such welders.")]
        public string ProjectionBuildsWaiting => Optimizer.Optimizations.WelderOptimization.Waiting;

        
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
        
        [DisplayTab(Name = "Freezer logs", GroupName = "Logs", Tab = "Logs", Order = 1, Description = "Every grid frozen and woken up (off by default)")]
        public bool EnableDebugLogs { get => _enableDebugLogs; set => SetValue(ref _enableDebugLogs, value); }
        
        [DisplayTab(Name = "Delay before freeze in sec", GroupName = "Freezer", Tab = "Freezer", Order = 8, Description = "Delay before freeze in sec")]
        public int DelayBeforeFreezeSec { get => _delayBeforeFreezeSec; set => SetValue(ref _delayBeforeFreezeSec, value); }
        
        [DisplayTab(Name = "Delay before freezer start in sec", GroupName = "Freezer", Tab = "Freezer", Order = 7, Description = "Delay before freezer start in sec")]
        public int DelayBeforeFreezerStartSec { get => _delayBeforeFreezerStartSec; set => SetValue(ref _delayBeforeFreezerStartSec, value); }

        [DisplayTab(Name = "Compensation logs", GroupName = "Logs", Tab = "Logs", Order = 2, Description = "What frozen blocks are given for the time they were frozen (off by default)")]
        public bool EnableCompensationLogs { get => _enableCompensationLogs; set => SetValue(ref _enableCompensationLogs, value); }

        [DisplayTab(Name = "Diagnostic logs", GroupName = "Logs", Tab = "Logs", Order = 0,
            Description = "Timings and what the optimisations did (warm-ups, save and voxel timings, deferred deletes, economy steps...). Off by default; errors are logged anyway")]
        public bool DiagnosticLogs { get => _diagnosticLogs; set => SetValue(ref _diagnosticLogs, value); }
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
        
        
        [DisplayTab(Name = "Physics threads", GroupName = "Other", Tab = "Other", Order = 3,
            Description = "Havok worker threads for the physics step. Vanilla lets Havok pick (7 on a 16-thread CPU); default is 80% of the logical processors. Takes effect after a restart")]
        public int PhysicsThreads { get => _physicsThreads; set => SetValue(ref _physicsThreads, value); }

        [DisplayTab(Name = "Game thread on fast cores", GroupName = "Other", Tab = "Other", Order = 4,
            Description = "Keep the game thread on the CPU's fastest (favored) performance cores; a CPU whose cores are all alike is left alone. Takes effect after a restart")]
        public bool GameThreadOnFastCores { get => _gameThreadOnFastCores; set => SetValue(ref _gameThreadOnFastCores, value); }

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
        
        [DisplayTab(Name = "Scripts max ms per frame", GroupName = "Scripts", Tab = "Scripts", Order = 2,
            Description = "How much of every frame one script may own on average: a script on Update1 costs its run time every single frame, which no per-run limit can see. 0 turns the limit off")]
        public float ScriptsMaxMsPerFrame
        {
            get => _scriptsMaxMsPerFrame;
            set => SetValue(ref _scriptsMaxMsPerFrame, value);
        }

        [DisplayTab(Name = "Scripts overtime exec times before punish", GroupName = "Scripts", Tab = "Scripts", Order = 3, Description = "Scripts overtime exec times before punish")]
        public int ScriptOvertimeExecTimesBeforePunish
        {
            get => _scriptOvertimeExecTimesBeforePunish;
            set => SetValue(ref _scriptOvertimeExecTimesBeforePunish, value);
        }
        
        
    }
}