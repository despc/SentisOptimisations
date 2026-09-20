using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Havok;
using NAPI;
using Sandbox;
using Sandbox.Engine.Voxels;
using Sandbox.Engine.Physics;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations;
using SentisOptimisations.DelayedLogic;
using SentisOptimisations.Utils;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.Entity.EntityComponents.Interfaces;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace SentisOptimisationsPlugin.Freezer;

public class FreezeLogic
{
    private static int _wakeupTimeInSec = 10;

    // Written by the game thread (freeze/unfreeze actions, entity-removal observer) and read/
    // written by the background FreezerLoop; plain HashSet would race and can throw or tear.
    public static ConcurrentHashSet<long> FrozenGrids = new();
    public static ConcurrentHashSet<long> FrozenPhysicsGrids = new();
    public static ConcurrentHashSet<long> InFreezeQueue = new();
    // Simulation frame at which each grid was frozen (written on the game thread at freeze time).
    public static readonly ConcurrentDictionary<long, ulong> FrozenAtFrame = new();
    private static readonly Dictionary<long, DateTime> WakeUpDatas = new(); //EntityId:NextWakeUpTime
    private static readonly object _wakeUpLock = new();

    // sanity cap for one compensation: at 60 fps this is ~8 hours of simulation
    internal const ulong MaxCompensationFrames = 60UL * 60 * 8;

    // Written by the FreezerLoop thread, averaged by the same/other loops - lock it.
    private static readonly List<float> CpuLoads = new();
    private static readonly object CpuLoadLock = new();

    public void CheckGridGroup(HashSet<MyCubeGrid> grids)
    {
        try
        {
            var freezerEnabled = SentisOptimisationsPlugin.Config.FreezerEnabled;
            var anyGrid = grids.FirstElement();
            var gridsPosition = anyGrid.PositionComp.GetPosition();
            var isWakeUpTime = IsWakeUpTime(grids);
            var isAnyGridStatic = grids.Any(grid => grid.IsStatic);
            var freezeDistance = isAnyGridStatic
                ? SentisOptimisationsPlugin.Config.FreezeDistanceStatic
                : SentisOptimisationsPlugin.Config.FreezeDistanceDynamic;
            var stepped = IsPhysicsStepped(grids);
            // A player is present both where their character is and where whatever they control is:
            // somebody flying a ship from a remote control block keeps two places alive, and their
            // own surroundings used to stay frozen because a player's position is the ship's.
            if (!freezerEnabled || stepped && PlayerAnchors.AnyInRadius(gridsPosition, freezeDistance)
                || WakeRequests.IsAwake(grids)
                || isWakeUpTime)
            {
                UnfreezeGrids(grids, isWakeUpTime);
                return;
            }

            FreezeGrids(grids, now: !stepped);
        }
        catch (InvalidOperationException e)
        {
            // ignore "Collection was modified"
        }
    }

    // With EnableSelectivePhysicsUpdates, MyPhysics steps only the Havok worlds (clusters) that hold a
    // character or something replicated to a client. A group awake in logic in a world that is not
    // stepped runs its pistons, rotors and thrusters against physics that stands still, and the
    // constraints snap when the world is stepped again: a kick of several m/s, 15 m in freezer_physics
    // (the world stops the moment the player leaves, the freeze came 5 s later; on the way back the
    // thaw came before the grids were streamed to the player). So a group thaws only while its world is
    // stepped and freezes at once when it is not. The set is taken on the game thread once per freezer
    // pass (RefreshSteppedWorlds); null means every world is stepped (the setting is off).
    private static volatile HashSet<HkWorld> _steppedWorlds;
    // Every world the snapshot saw. Havok worlds are made anew when clusters are rebuilt; a world the
    // snapshot has not seen yet is taken as stepped, or a group next to a player froze on the spot.
    private static volatile HashSet<HkWorld> _knownWorlds;
    private static readonly System.Reflection.FieldInfo WorldObserverField =
        typeof(MyPhysics).GetField("m_worldObserver", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    private static readonly System.Reflection.MethodInfo IsClusterActiveMethod =
        typeof(MyPhysics).GetMethod("IsClusterActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, new[] { typeof(int), typeof(int) }, null);
    private static MyPhysics _boundPhysics;
    private static Func<int, int, bool> _isClusterActive;

    /// <summary>Takes the set of Havok worlds MyPhysics steps. Game thread.</summary>
    public static void RefreshSteppedWorlds()
    {
        try
        {
            var physics = MySession.Static?.GetComponent<MyPhysics>();
            if (physics == null || WorldObserverField == null || IsClusterActiveMethod == null || MyPhysics.Clusters == null ||
                WorldObserverField.GetValue(physics) == null)
            {
                _steppedWorlds = null;
                _knownWorlds = null;
                return;
            }

            if (_boundPhysics != physics)
            {
                _isClusterActive = (Func<int, int, bool>)Delegate.CreateDelegate(typeof(Func<int, int, bool>), physics, IsClusterActiveMethod);
                _boundPhysics = physics;
            }

            var stepped = new HashSet<HkWorld>();
            var known = new HashSet<HkWorld>();
            foreach (var cluster in MyPhysics.Clusters.GetClusters())
            {
                if (!(cluster.UserData is HkWorld world)) continue;
                known.Add(world);
                if (_isClusterActive(cluster.ClusterId, world.CharacterRigidBodies.Count)) stepped.Add(world);
            }
            _knownWorlds = known;
            _steppedWorlds = stepped;
        }
        catch (Exception e)
        {
            _steppedWorlds = null;
            _knownWorlds = null;
            SentisOptimisationsPlugin.Log.Error(e, "Stepped Havok worlds");
        }
    }

    private static bool IsPhysicsStepped(HashSet<MyCubeGrid> grids)
    {
        var stepped = _steppedWorlds;
        var known = _knownWorlds;
        if (stepped == null || known == null) return true;
        foreach (var grid in grids)
        {
            var world = grid.Physics?.HavokWorld;
            if (world == null || stepped.Contains(world) || !known.Contains(world)) return true;
        }

        return false;
    }

    private bool IsWakeUpTime(HashSet<MyCubeGrid> grids)
    {
        var minEntityId = grids.MinBy(grid => grid.EntityId).EntityId;
        if (WakeUpDatas.TryGetValue(minEntityId, out var data))
        {
            if (DateTime.Now < data && data < DateTime.Now.AddSeconds(_wakeupTimeInSec))
            {
                if (GetAvgCpuLoad() > 70)
                {
                    WakeUpDatas[minEntityId] = DateTime.Now.AddSeconds(SentisOptimisationsPlugin.Config.MinWakeUpIntervalInSec);
                    return false;
                }
                // grids.ForEach(grid => Log("Wake up time, grid - " + grid.DisplayName));

                return true;
            }
        }

        return false;
    }

    private void UnfreezeGrids(HashSet<MyCubeGrid> grids, bool isWakeUpTime)
    {
        var minEntityId = grids.MinBy(grid => grid.EntityId).EntityId;
        InFreezeQueue.Remove(minEntityId);
        if (!grids.Any(grid => FrozenGrids.Contains(grid.EntityId))) return;

        // Which grids to thaw is decided on the game thread, like the freeze: read from here, a freeze
        // being applied grid by grid on the game thread showed only part of the group as frozen, only that
        // part got its bodies back to dynamic, and the rest stayed fixed with constraints to moving grids
        // (a top torn off in freezer_stress).
        MyAPIGateway.Utilities.InvokeOnGameThread(() =>
        {
            foreach (var grid in grids)
            {
                if (grid.Closed || grid.MarkedForClose || grid.IsPreview || grid.Parent != null) continue;
                if (!FrozenGrids.Contains(grid.EntityId)) continue;
                if (!isWakeUpTime)
                {
                    lock (_wakeUpLock)
                    {
                        WakeUpDatas.Remove(grid.EntityId);
                    }
                }

                Log("Unfreeze grid " + grid.DisplayName);
                FrozenGridSaveCache.Invalidate(grid.EntityId);
                FrozenGrids.Remove(grid.EntityId);
                FrozenAtFrame.TryRemove(grid.EntityId, out _);
                InFreezeQueue.Remove(grid.EntityId);
                CompensateFrozenFrames(grid);

                // Whatever the config or the group is now: a body the freezer made fixed goes back to
                // dynamic. Gating this on FreezePhysics or on "the group has a fixed grid" left bodies
                // fixed for good on a non-static grid once either changed while it was frozen.
                DoUnfreezePhysics(grid);
                RegisterRecursive(grid);
                grid.PlayerPresenceTier = MyUpdateTiersPlayerPresence.Normal;
            }
        });
    }

    private static void CompensateFrozenFrames(MyCubeGrid grid)
    {
        var frame = MySandboxGame.Static.SimulationFrameCounter;
        FrozenProduction.OnThawed(grid, frame);
        foreach (var myCubeBlock in grid.GetFatBlocks())
        {
            if (!(myCubeBlock is MyFunctionalBlock))
            {
                continue;
            }

            var blockId = myCubeBlock.EntityId;
            var needToCompensate = NeedToCompensate((MyFunctionalBlock)myCubeBlock);
            if (needToCompensate)
            {
                MyTimerComponent timer =
                    (MyTimerComponent)myCubeBlock.easyGetField("m_timer", typeof(MyFunctionalBlock));
                if (timer != null)
                {
                    // Accumulate the frozen period; a previous pending period is NOT overwritten.
                    if (!CompensationTracker.OnUnfrozen(blockId, frame, MaxCompensationFrames))
                        continue;
                    if (!CompensationTracker.TryScheduleApply(blockId))
                        continue; // an already-scheduled apply will pick everything up

                    // Apply after the unfrozen blocks have run a couple of normal frames. The
                    // apply takes the ACCUMULATED total atomically; if the block got frozen
                    // again in the meantime the total survives for the next apply.
                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    {
                        try
                        {
                            if (CompensationTracker.IsFrozen(blockId))
                            {
                                CompensationTracker.ReleaseSchedule(blockId);
                                return;
                            }

                            if (myCubeBlock.Closed || myCubeBlock.MarkedForClose)
                            {
                                CompensationTracker.Forget(blockId);
                                return;
                            }

                            if (CompensationTracker.TryTakeCompensation(blockId, out var framesAfterFreeze))
                            {
                                // ADD to whatever vanilla accumulated during the apply window
                                // instead of overwriting: overwriting silently dropped the
                                // ~2 s of real production of that window.
                                var vanilla = timer.FramesFromLastTrigger;
                                timer.FramesFromLastTrigger =
                                    uint.MaxValue - vanilla < framesAfterFreeze
                                        ? uint.MaxValue
                                        : vanilla + framesAfterFreeze;
                                grid.PlayerPresenceTier = MyUpdateTiersPlayerPresence.Normal;
                            }
                        }
                        catch (Exception ex)
                        {
                            CompensationTracker.ReleaseSchedule(blockId);
                            SentisOptimisationsPlugin.Log.Error(ex, "Compensate exception");
                        }
                    }, StartAt: (int)(frame + 120));
                }
            }
            else
            {
                CompensationTracker.CancelPending(blockId);
            }
        }
    }

    /// <param name="now">Without the DelayBeforeFreezeSec wait: the group's Havok world is not stepped.</param>
    private void FreezeGrids(HashSet<MyCubeGrid> grids, bool now = false)
    {
        var configAntifreezeBlocksSubtypes = SentisOptimisationsPlugin.Config.AntifreezeBlocksSubtypes;
        var antifreezeBlocksSubtypes = configAntifreezeBlocksSubtypes.Split(':');
        foreach (var grid in grids)
        {
            if (!string.IsNullOrEmpty(configAntifreezeBlocksSubtypes) && grid.GetBlocks().Any(block =>
                    Enumerable.Contains(antifreezeBlocksSubtypes, block.BlockDefinition.Id.SubtypeName)))
            {
                // Log("Found antifreeze block, skip grid " + grid.DisplayName);
                return;
            }

            if (!SentisOptimisationsPlugin.Config.FreezeSignals && (grid.DisplayName.Contains("Container MK-") ||
                                                                    grid.DisplayName.Contains("Container_MK-")))
            {
                return;
            }

            if (!SentisOptimisationsPlugin.Config.FreezeNpc && grid.isNpcGrid())
            {
                // Log("Dont freeze NPC, skip grid " + grid.DisplayName);
                return;
            }
        }

        bool needToAwake = false;
        foreach (var myCubeGrid in grids)
        {
            if (myCubeGrid.GetFatBlocks()
                .Any(block => block is MyFunctionalBlock && NeedToCompensate((MyFunctionalBlock)block)))
            {
                needToAwake = true;
                break;
            }
        }

        var minEntityId = grids.MinBy(grid => grid.EntityId).EntityId;
        if (needToAwake)
        {
            lock (_wakeUpLock)
            {
                if (WakeUpDatas.TryGetValue(minEntityId, out var dateTime))
                {
                    if (DateTime.Now.AddSeconds(_wakeupTimeInSec) > dateTime)
                    {
                        WakeUpDatas[minEntityId] = DateTime.Now +
                                                   TimeSpan.FromSeconds(
                                                       SentisOptimisationsPlugin.Config.MinWakeUpIntervalInSec +
                                                       minEntityId % SentisOptimisationsPlugin.Config
                                                           .MinWakeUpIntervalInSec);
                    }
                    else if (DateTime.Now < dateTime && dateTime < DateTime.Now.AddSeconds(_wakeupTimeInSec))
                    {
                        return;
                    }
                }
                else
                {
                    WakeUpDatas.Add(minEntityId,
                        DateTime.Now + TimeSpan.FromSeconds(SentisOptimisationsPlugin.Config.MinWakeUpIntervalInSec +
                                                            minEntityId % SentisOptimisationsPlugin.Config
                                                                .MinWakeUpIntervalInSec));
                }
            }
        }

        // Add returns false when the id is already queued (ConcurrentDictionary.TryAdd)
        if (!InFreezeQueue.Add(minEntityId))
        {
            return;
        }

        var delayBeforeFreezeSec = now ? 0 : SentisOptimisationsPlugin.Config
            .DelayBeforeFreezeSec;
        var needToFreezeGrids = grids.Where(grid => !FrozenGrids.Contains(grid.EntityId)).ToList();

        if (needToFreezeGrids.Count == 0)
        {
            InFreezeQueue.Remove(minEntityId);
            return;
        }

        Thread.Sleep(8);
        // Everything is decided on the game thread at the moment of the freeze: during the delay a
        // player may come (the unfreeze drops the queue entry) and the group may change (a gear
        // locks, a grid joins). The queue entry is dropped only there, so the check sees it.
        DelayedProcessor.Instance.AddDelayedAction(DateTime.Now.AddSeconds(delayBeforeFreezeSec), () =>
            MyAPIGateway.Utilities.InvokeOnGameThread(() =>
            {
                try
                {
                    if (!InFreezeQueue.Contains(minEntityId)) return;
                    var realyNeedToFreezeGrids = grids.Where(grid =>
                        grid != null && !grid.Closed && !grid.MarkedForClose && !grid.IsPreview &&
                        grid.Parent == null && !FrozenGrids.Contains(grid.EntityId)).ToList();
                    var freezePhysics = SentisOptimisationsPlugin.Config.FreezePhysics && CanFreezePhysics(grids);
                    var frame = MySandboxGame.Static.SimulationFrameCounter;
                    foreach (var grid in realyNeedToFreezeGrids)
                    {
                        Log("Freeze grid " + grid.DisplayName);

                        if (!grid.IsStatic)
                        {
                            var gridPhysics = grid.Physics;
                            if (gridPhysics != null)
                            {
                                gridPhysics.SetSpeeds(Vector3.Zero, Vector3.Zero);
                                if (freezePhysics)
                                {
                                    DoFreezePhysics(grid);
                                }
                                grid.RaisePhysicsChanged();
                            }
                        }

                        FrozenGrids.Add(grid.EntityId);
                        FrozenAtFrame[grid.EntityId] = frame;
                        UnregisterRecursive(grid);

                        // Gas generators, oxygen farms and farm plots are not production blocks:
                        // their catch-up is its own thing (FrozenProduction).
                        FrozenProduction.OnFrozen(grid, frame);

                        // Stamp the compensation clock at the exact frame the grid stops updating.
                        // Blocks that leave the world must drop their stamp (see ForgetGrid +
                        // EntitiesObserver): EntityIds are reused.
                        foreach (var myCubeBlock in grid.GetFatBlocks())
                        {
                            if (myCubeBlock is MyFunctionalBlock functional && NeedToCompensate(functional))
                            {
                                CompensationTracker.OnFrozen(myCubeBlock.EntityId, frame);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    SentisOptimisationsPlugin.Log.Error(e, "Freeze grids");
                }
                finally
                {
                    InFreezeQueue.Remove(minEntityId);
                }
            }));
    }

    /// <summary>
    /// Drop every trace of grids that left the world. A stale freeze stamp under a recycled
    /// EntityId would hand the next holder of that id an absurd compensation delta.
    /// </summary>
    public static void ForgetGrid(MyCubeGrid grid)
    {
        FrozenGridSaveCache.Invalidate(grid.EntityId);
        FrozenGrids.Remove(grid.EntityId);
        FrozenAtFrame.TryRemove(grid.EntityId, out _);
        FrozenPhysicsGrids.Remove(grid.EntityId);
        InFreezeQueue.Remove(grid.EntityId);
        lock (_wakeUpLock)
        {
            WakeUpDatas.Remove(grid.EntityId);
        }

        try
        {
            foreach (var block in grid.GetFatBlocks())
            {
                CompensationTracker.Forget(block.EntityId);
                FrozenProduction.Forget(block.EntityId);
            }
        }
        catch (Exception e)
        {
            // grid already torn down; stamps for its blocks are dropped below on sweep
        }
    }

    /// <summary>
    /// The physics of a group is frozen only as a whole and only if nothing holds it in place: no
    /// static grid and no landing gear locked to voxels (those stay dynamic, logic frozen). The
    /// group is read now, on the game thread: a grid that joined it during the freeze delay would
    /// otherwise stay dynamic, held by constraints to fixed bodies, and so would a group where
    /// some grids are already frozen without their physics (unless the whole group is being
    /// switched over at once, <paramref name="wholeGroup"/>, as when FreezePhysics is turned on).
    /// </summary>
    private static bool CanFreezePhysics(HashSet<MyCubeGrid> grids, bool wholeGroup = false)
    {
        var any = grids.FirstOrDefault(grid => grid != null && !grid.Closed && !grid.MarkedForClose);
        if (any == null) return false;
        var current = new List<IMyCubeGrid>();
        MyAPIGateway.GridGroups.GetGroup(any, GridLinkTypeEnum.Physical, current);
        var group = current.Cast<MyCubeGrid>().ToHashSet();
        if (group.Any(grid => !grids.Contains(grid) ||
                              !wholeGroup && FrozenGrids.Contains(grid.EntityId) && !FrozenPhysicsGrids.Contains(grid.EntityId)))
            return false;
        return !GroupContainsFixedGrid(group);
    }

    private static bool GroupContainsFixedGrid(HashSet<MyCubeGrid> grids)
    {
        try
        {
            return grids.Any(grid =>
            {
                if (grid.IsStatic)
                {
                    return true;
                }

                if (grid.Physics == null)
                {
                    return false;
                }

                return new HashSet<HkConstraint>(grid.Physics.Constraints).Any(constraint =>
                {
                    if (constraint.RigidBodyB == null)
                    {
                        return false;
                    }

                    return constraint.RigidBodyB.UserObject is MyVoxelPhysicsBody;
                });
            });
        }
        catch (Exception e)
        {
            return true;
        }
    }

    // MyGridPhysics.ConvertToStatic puts the now fixed body into the world's set of active bodies
    // (HkWorld.RigidBodyActivated), and Havok never deactivates a fixed body: every physics-frozen
    // grid stayed in that set for as long as it was frozen, and MyPhysics.UpdateActiveRigidBodies
    // walked all of them every frame (~5 ms a frame with 1250 frozen grids). The private
    // HkWorld.RigidBodyDeactivated takes the body out the way a deactivation does (nothing else
    // listens to it); ConvertToDynamic puts it back on thaw.
    // Null if a game update renames it: frozen bodies then just stay in the set, as before.
    internal static readonly Action<HkWorld, HkEntity> RigidBodyDeactivated =
        typeof(HkWorld).GetMethod("RigidBodyDeactivated",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, new[] { typeof(HkEntity) }, null) is { } method
            ? (Action<HkWorld, HkEntity>)Delegate.CreateDelegate(typeof(Action<HkWorld, HkEntity>), method, false)
            : null;

    private static void DoFreezePhysics(MyCubeGrid grid)
    {
        try
        {
            var gridPhysics = grid.Physics;
            gridPhysics.ConvertToStatic();
            FrozenPhysicsGrids.Add(grid.EntityId);
            var body = gridPhysics.RigidBody;
            if (RigidBodyDeactivated != null && body != null && body.IsFixed && body.InWorld && gridPhysics.HavokWorld != null)
                RigidBodyDeactivated(gridPhysics.HavokWorld, body);
        }
        catch (Exception e)
        {
            SentisOptimisationsPlugin.Log.Error(e, "Freeze physics " + grid.DisplayName);
        }
    }

    private static void DoUnfreezePhysics(MyCubeGrid grid)
    {
        if (!FrozenPhysicsGrids.Remove(grid.EntityId))
        {
            return;
        }

        try
        {
            var gridPhysics = grid.Physics;
            if (grid.IsStatic || gridPhysics == null || !gridPhysics.IsStatic) return;
            gridPhysics.ConvertToDynamic(grid.GridSizeEnum == MyCubeSize.Large, grid.IsClientPredicted);
            gridPhysics.SetSpeeds(Vector3.Zero, Vector3.Zero);
            grid.RecalculateGravity();
            grid.RaisePhysicsChanged();
        }
        catch (Exception e)
        {
            SentisOptimisationsPlugin.Log.Error(e, "Unfreeze physics " + grid.DisplayName);
        }
    }

    public static bool NeedToCompensate(MyFunctionalBlock myCubeBlock)
    {
        if (myCubeBlock == null || !myCubeBlock.IsWorking)
        {
            return false;
        }

        var subtypeName = myCubeBlock.BlockDefinition.Id.SubtypeName;
        if (!string.IsNullOrEmpty(subtypeName))
        {
            if (subtypeName.Contains("Crusher"))
            {
                return false;
            }
        }
        var needToCompensate = myCubeBlock is MyProductionBlock;
        return needToCompensate;
    }

    private void UnregisterRecursive(MyEntity e)
    {
        MyEntities.UnregisterForUpdate(e);
        (e.GameLogic as IMyGameLogicComponent)?.UnregisterForUpdate();
        e.Flags |= (EntityFlags)4;
        if (e.Hierarchy == null) return;

        foreach (var child in e.Hierarchy.Children) UnregisterRecursive((MyEntity)child.Container.Entity);
    }

    private void RegisterRecursive(MyEntity e)
    {
        MyEntities.RegisterForUpdate(e);
        (e.GameLogic as IMyGameLogicComponent)?.RegisterForUpdate();
        e.Flags &= ~(EntityFlags)4;
        if (e.Hierarchy == null) return;

        foreach (var child in e.Hierarchy.Children) RegisterRecursive((MyEntity)child.Container.Entity);
    }


    public static void Log(string message)
    {
        if (SentisOptimisationsPlugin.Config.EnableDebugLogs)
        {
            SentisOptimisationsPlugin.Log.Warn(message);
        }
    }

    public static void CompensationLogs(string message)
    {
        if (SentisOptimisationsPlugin.Config.EnableCompensationLogs)
        {
            SentisOptimisationsPlugin.Log.Warn(message);
        }
    }

    public void UpdateCpuLoad(float cpuLoad)
    {
        lock (CpuLoadLock)
        {
            while (CpuLoads.Count > 30)
            {
                CpuLoads.RemoveAt(0);
            }

            CpuLoads.Add(cpuLoad);
        }
    }

    public static void UpdateFreezePhysics(bool freezePhysicsEnabled)
    {
        DelayedProcessor.Instance.AddDelayedAction(DateTime.Now, () =>
        {
            MyAPIGateway.Utilities.InvokeOnGameThread(() =>
            {
                var gridsList = new HashSet<long>(FrozenGrids);
                int i = 0;


                List<HashSet<IMyCubeGrid>> groups = new List<HashSet<IMyCubeGrid>>();
                while (gridsList.Count > 0)
                {
                    var gridEntityId = gridsList.FirstElement();
                    if (!(MyEntities.GetEntityById(gridEntityId) is MyCubeGrid grid))
                    {
                        gridsList.Remove(gridEntityId);
                        continue;
                    }
                    HashSet<IMyCubeGrid> group = new HashSet<IMyCubeGrid>();
                    MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Physical, group);
                    group.ForEach(cubeGrid => gridsList.Remove(cubeGrid.EntityId));
                    groups.Add(group);
                }

                foreach (var group in groups)
                {
                    i += 2;
                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    {
                        var grids = group.Select(cubeGrid => (MyCubeGrid)cubeGrid).ToHashSet();
                        // Only a group frozen as a whole gets its physics frozen (see CanFreezePhysics).
                        var freezePhysics = freezePhysicsEnabled &&
                                            grids.All(g => FrozenGrids.Contains(g.EntityId)) &&
                                            CanFreezePhysics(grids, wholeGroup: true);
                        foreach (var myCubeGrid in grids)
                        {
                            if (myCubeGrid.Closed || myCubeGrid.Physics == null || myCubeGrid.IsStatic)
                            {
                                continue;
                            }

                            if (freezePhysics)
                            {
                                if (FrozenPhysicsGrids.Contains(myCubeGrid.EntityId)) continue;
                                myCubeGrid.Physics.SetSpeeds(Vector3.Zero, Vector3.Zero);
                                DoFreezePhysics(myCubeGrid);
                            }
                            else if (!freezePhysicsEnabled)
                            {
                                DoUnfreezePhysics(myCubeGrid);
                            }
                        }
                    }, StartAt: (int)MySandboxGame.Static.SimulationFrameCounter + i);
                }
            });
        });
    }
    
    
    public static float GetAvgCpuLoad()
    {
        float sum = 0;
        int count;
        lock (CpuLoadLock)
        {
            count = CpuLoads.Count;
            for (var i = 0; i < count; i++)
            {
                sum += CpuLoads[i];
            }
        }

        return (float)Math.Round((decimal)(count > 0 ? sum / count : 0.0), 1);
    }
}