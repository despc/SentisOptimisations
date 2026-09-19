using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NAPI;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisationsPlugin.ShipTool;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace Optimizer.Optimizations
{
    [PatchShim]
    public static class WelderOptimization
    {
        private static readonly ProjectionBuildBudget ProjectionBudget = new ProjectionBuildBudget();
        private static readonly Dictionary<long, ProjectionFrontierState> ProjectionFrontiers =
            new Dictionary<long, ProjectionFrontierState>();
        private static readonly Vector3I[] NeighborDirections =
        {
            Vector3I.Left, Vector3I.Right, Vector3I.Up, Vector3I.Down,
            Vector3I.Forward, Vector3I.Backward,
        };

        private sealed class ProjectionFrontierState
        {
            public MyCubeGrid Preview;
            public readonly List<Vector3I> Positions = new List<Vector3I>();
            public readonly ProjectionFrontierCursor Cursor = new ProjectionFrontierCursor();
            public readonly Queue<Vector3I> Ready = new Queue<Vector3I>();
            public readonly HashSet<Vector3I> Queued = new HashSet<Vector3I>();
            public long LastSeenFrame;

            public void Reset(MyCubeGrid preview)
            {
                Preview = preview;
                Positions.Clear();
                if (preview != null)
                    foreach (var block in preview.CubeBlocks) Positions.Add(block.Position);
                Cursor.Reset();
                Ready.Clear();
                Queued.Clear();
            }

            public void Enqueue(Vector3I position)
            {
                if (Queued.Add(position)) Ready.Enqueue(position);
            }
        }

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("WelderOptimization", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var MethodActivate = typeof(MyShipWelder).GetMethod
                ("Activate", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodActivate).Prefixes.Add(
                typeof(WelderOptimization).GetMethod(nameof(Activate),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool Activate(MyShipWelder __instance, ref bool __result, HashSet<MySlimBlock> targets)
        {
        try
        {
            __result = false; //it affects only sound;
            if (!SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.WelderTweaksEnabled)
            {
                return true;
            }

            var def = (MyShipWelderDefinition) __instance.BlockDefinition;
            if (def.SensorRadius < 0.01f) //NanobotOptimization
            {
                return false;
            }

            if (!SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.WelderTweaksSelfWelding)
            {
                targets.Remove(__instance.SlimBlock);
            }

            ActivateInternal(__instance, targets);
            return false;
        


            }
                catch (Exception __guard_e)
                {
                    SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error("Activate exception " + __guard_e);
                    return true;  // fall back to vanilla behavior
                }
        }


        // Welders activate on the game thread only, one at a time, so the scratch collections of an
        // activation are shared instead of allocated for each of them: an idle welder activates
        // every quarter second whether or not it finds anything to weld.
        private static readonly Dictionary<string, int> MissingComponents = new Dictionary<string, int>();
        private static readonly HashSet<MySlimBlock> TargetsToWeld = new HashSet<MySlimBlock>();
        private static readonly List<MyWelder.ProjectionRaycastData> ProjectedBlocks = new List<MyWelder.ProjectionRaycastData>();
        private static readonly Dictionary<MyDefinitionId, int> ComponentsToPull = new Dictionary<MyDefinitionId, int>();

        public static void ActivateInternal(MyShipWelder welder, HashSet<MySlimBlock> targets)
        {
            if (welder.MarkedForClose || welder.Closed)
            {
                return;
            }

            try
            {
                ActivateInternal(welder, targets, MissingComponents, TargetsToWeld);
            }
            finally
            {
                MissingComponents.Clear();
                TargetsToWeld.Clear();
            }
        }

        private static void ActivateInternal(MyShipWelder welder, HashSet<MySlimBlock> targets,
            Dictionary<string, int> m_missingComponents, HashSet<MySlimBlock> targetsToWeld)
        {
            int num = targets.Count;
            m_missingComponents.Clear();
            targetsToWeld.Clear();
            int i = 0;
            foreach (MySlimBlock mySlimBlock in targets)
            {
                if (mySlimBlock.IsFullIntegrity)
                {
                    num--;
                }
                else
                {
                    MyCubeBlockDefinition.PreloadConstructionModels(mySlimBlock.BlockDefinition);
                    mySlimBlock.GetMissingComponents(m_missingComponents);
                    targetsToWeld.Add(mySlimBlock);
                    i++;
                    if (i > 15) break;
                }
            }

            targets = targetsToWeld;
            MyInventory inventory = welder.GetInventory(0);
            foreach (KeyValuePair<string, int> keyValuePair in m_missingComponents)
            {
                MyDefinitionId myDefinitionId = new MyDefinitionId(typeof(MyObjectBuilder_Component), keyValuePair.Key);
                if (Math.Max(
                        keyValuePair.Value - (int) inventory.GetItemAmount(myDefinitionId, MyItemFlags.None, false),
                        0) !=
                    0 && welder.UseConveyorSystem)
                {
                    welder.CubeGrid.GridSystems.ConveyorSystem.PullItem(myDefinitionId,
                        new MyFixedPoint?(keyValuePair.Value), welder, inventory, false, false);
                }
            }

            m_missingComponents.Clear();

            // AsyncWeld v2: the weld itself (IncreaseMountLevel, inventory, conveyors, networking)
            // must run on the game thread; it already does here. Queuing it on a worker only
            // created data races, so the inline path is the only path.
            var welded = Weld(welder, targets, inventory, num);

            WeldProjectionsWithWelding(welder, welded);
        }

        private static void WeldProjectionsWithWelding(MyShipWelder welder, bool welded)
        {
            if (!welded || SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config
                    .WelderTweaksCanWeldProjectionsIfWeldedOtherBlocks)
            {
                WeldProjections(welder);
            }
        }


        public static bool Weld(MyShipWelder welder, HashSet<MySlimBlock> targets, MyInventory inventory,
            int foundBlocks)
        {
            float blocksToWeld = Math.Min(4, (foundBlocks > 0) ? foundBlocks : 1);
            float weldAmount = 10 * MySession.Static.WelderSpeedMultiplier * MyShipWelder.WELDER_AMOUNT_PER_SECOND *
                (MyShipGrinderConstants.GRINDER_COOLDOWN_IN_MILISECONDS * 0.001f) / blocksToWeld;
            float maxAllowedBoneMovement = MyShipWelder.WELDER_MAX_REPAIR_BONE_MOVEMENT_SPEED *
                                           (MyShipGrinderConstants.GRINDER_COOLDOWN_IN_MILISECONDS * 0.001f);


            bool weldedAnyThing = false;
            foreach (var block in targets)
            {
                if (block.CubeGrid.Physics != null && block.CubeGrid.Physics.Enabled)
                {
                    bool canWeld = false;
                    bool? flag2 = block.ComponentStack.WillFunctionalityRise(weldAmount);
                    if (flag2 == null || !flag2.Value || MySession.Static.CheckLimitsAndNotify(
                            MySession.Static.LocalPlayerId, block.BlockDefinition.BlockPairName,
                            block.BlockDefinition.PCU - MyCubeBlockDefinition.PCU_CONSTRUCTION_STAGE_COST, 0, 0,
                            null))
                    {
                        canWeld = true;
                    }

                    if (canWeld)
                    {
                        // already on the game thread: weld directly (was: InvokeOnGameThread per block)
                        {
                            MySlimBlock blockToWeld = block;
                            try
                            {
                                blockToWeld.MoveItemsToConstructionStockpile(inventory);
                                blockToWeld.MoveUnneededItemsFromConstructionStockpile(inventory);
                                if (blockToWeld.HasDeformation || blockToWeld.MaxDeformation > 0.0001f ||
                                    !blockToWeld.IsFullIntegrity)
                                {
                                    blockToWeld.IncreaseMountLevel(weldAmount, welder.OwnerId, inventory,
                                        maxAllowedBoneMovement, welder.HelpOthers, welder.IDModule.ShareMode, false,
                                        false);

                                    weldedAnyThing = true;
                                }
                            }
                            catch (Exception e)
                            {
                                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e);
                            }
                        }
                    }
                }
            }


            return weldedAnyThing;
        }

        public static void WeldProjections(MyShipWelder welder)
        {
            if (welder.MarkedForClose || welder.Closed)
            {
                return;
            }

            // Do not run the expensive projector/CanBuild scan when another welder has already
            // spent the global materialization budget for this simulation frame.
            if (!ProjectionBudget.CanConsume(MySession.Static.GameplayFrameCounter,
                    SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.ProjectionBuildsPerFrame,
                    welder.EntityId))
                return;

            try
            {
                var array = FindProjectedBlocks(welder);
                DoWeldProjections(welder, array);
            }
            finally
            {
                ProjectedBlocks.Clear();
                ComponentsToPull.Clear();
            }
        }

        private static void DoWeldProjections(MyShipWelder welder, List<MyWelder.ProjectionRaycastData> array)
        {
            if (array == null || array.Count == 0) return;
            MyInventory inventory = welder.GetInventory(0);
            if (welder.UseConveyorSystem && !MySession.Static.CreativeMode)
            {
                // Conveyor PullItem is a bulk staging operation; pulling one item immediately
                // before ContainItems does not reliably make a new component type available.
                // Aggregate only the current buildable frontier. The expensive grid mutation is
                // still governed independently by the global per-frame Build budget below.
                var componentsToPull = ComponentsToPull;
                componentsToPull.Clear();
                foreach (var candidate in array)
                {
                    var candidateComponents = candidate.hitCube.BlockDefinition.Components;
                    if (candidateComponents == null || candidateComponents.Length == 0) continue;
                    componentsToPull.Sum(candidateComponents[0].Definition.Id, 1);
                }
                foreach (var component in componentsToPull)
                    welder.CubeGrid.GridSystems.ConveyorSystem.PullItem(component.Key,
                        new MyFixedPoint?(Math.Min(component.Value, 8)), welder, inventory, false, false);
            }

            foreach (MyWelder.ProjectionRaycastData projectionRaycastData in array)
            {
                var components = projectionRaycastData.hitCube.BlockDefinition.Components;
                if (components == null || components.Length == 0)
                    continue;

                var componentId = components[0].Definition.Id;
                if (!welder.IsWithinWorldLimits(projectionRaycastData.cubeProjector,
                        projectionRaycastData.hitCube.BlockDefinition.BlockPairName,
                        projectionRaycastData.hitCube.BlockDefinition.PCU))
                    continue;
                if (!MySession.Static.CreativeMode &&
                    !inventory.ContainItems(new MyFixedPoint?(1), componentId, MyItemFlags.None))
                    continue;

                // This is deliberately global, not per welder: multiple tools are updated in
                // one simulation frame and their synchronous Build costs otherwise stack.
                if (!ProjectionBudget.TryConsume(MySession.Static.GameplayFrameCounter,
                        SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.ProjectionBuildsPerFrame,
                        welder.EntityId))
                    break;

                // game thread already: build the projection inline
                MyWelder.ProjectionRaycastData invokedBlock = projectionRaycastData;
                try
                {
                    if (invokedBlock.cubeProjector.Closed ||
                        invokedBlock.cubeProjector.CubeGrid.Closed ||
                        invokedBlock.hitCube.FatBlock != null && invokedBlock.hitCube.FatBlock.Closed)
                        continue;
                    invokedBlock.cubeProjector.Build(invokedBlock.hitCube, welder.OwnerId,
                        welder.EntityId,
                        builtBy: welder.BuiltBy);
                    QueueProjectedNeighbors(invokedBlock.cubeProjector, invokedBlock.hitCube.Position);
                }
                catch (Exception e)
                {
                    SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e);
                }
            }
        }

        private static void QueueProjectedNeighbors(MyProjectorBase projector, Vector3I builtPosition)
        {
            if (projector == null || projector.ProjectedGrid == null) return;
            ProjectionFrontierState state;
            if (!ProjectionFrontiers.TryGetValue(projector.EntityId, out state)) return;
            foreach (var direction in NeighborDirections)
            {
                var position = builtPosition + direction;
                if (projector.ProjectedGrid.GetCubeBlock(position) != null) state.Enqueue(position);
            }
        }

        private static ProjectionFrontierState FrontierState(MyProjectorBase projector, MyCubeGrid preview,
            long frame)
        {
            ProjectionFrontierState state;
            if (!ProjectionFrontiers.TryGetValue(projector.EntityId, out state))
            {
                state = new ProjectionFrontierState();
                ProjectionFrontiers[projector.EntityId] = state;
            }
            if (state.Preview != preview || state.Positions.Count != preview.BlocksCount)
                state.Reset(preview);
            state.LastSeenFrame = frame;

            if (ProjectionFrontiers.Count > 32)
            {
                var stale = ProjectionFrontiers
                    .Where(pair => frame - pair.Value.LastSeenFrame > 600 || pair.Value.Preview == null ||
                                   pair.Value.Preview.Closed)
                    .Select(pair => pair.Key).ToList();
                foreach (var key in stale) ProjectionFrontiers.Remove(key);
            }
            return state;
        }

        private static List<MyWelder.ProjectionRaycastData> FindProjectedBlocks(MyShipWelder welder)
        {
            var w = welder.WorldMatrix;
            var d = (MyShipWelderDefinition) (welder.BlockDefinition);
            BoundingSphereD boundingSphereD = new BoundingSphereD(
                w.Translation + w.Forward * (welder.CubeGrid.GridSize * 1.5f + d.SensorOffset),
                ShipToolPatch.GetWelderRadius(welder));
            var list = ProjectedBlocks;
            list.Clear();
            List<MyEntity> entitiesInSphere = MyEntities.GetEntitiesInSphere(ref boundingSphereD);
            var frame = MySession.Static.GameplayFrameCounter;
            var checks = Math.Max(1, SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config
                .ProjectionChecksPerActivation);

            foreach (MyEntity myEntity in entitiesInSphere)
            {
                MyCubeGrid myCubeGrid = myEntity as MyCubeGrid;
                if (myCubeGrid != null && myCubeGrid.Projector != null)
                {
                    var projector = myCubeGrid.Projector;
                    var state = FrontierState(projector, myCubeGrid, frame);

                    // Validate a few high-value frontier cells first. Stale entries are cheap and
                    // bounded; successful builds enqueue only their six grid neighbours.
                    var readyChecks = Math.Min(4, state.Ready.Count);
                    while (readyChecks-- > 0 && list.Count == 0)
                    {
                        var position = state.Ready.Dequeue();
                        state.Queued.Remove(position);
                        var block = myCubeGrid.GetCubeBlock(position);
                        if (block != null &&
                            Vector3D.DistanceSquared(myCubeGrid.GridIntegerToWorld(position), boundingSphereD.Center) <=
                            boundingSphereD.Radius * boundingSphereD.Radius &&
                            projector.CanBuild(block, true) == BuildCheckResult.OK)
                        {
                            list.Add(new MyWelder.ProjectionRaycastData(BuildCheckResult.OK, block, projector));
                        }
                    }

                    if (list.Count == 0)
                    foreach (var index in state.Cursor.Take(state.Positions.Count, checks))
                    {
                        var position = state.Positions[index];
                        if (Vector3D.DistanceSquared(myCubeGrid.GridIntegerToWorld(position), boundingSphereD.Center) >
                            boundingSphereD.Radius * boundingSphereD.Radius) continue;
                        var block = myCubeGrid.GetCubeBlock(position);
                        if (block != null && projector.CanBuild(block, true) == BuildCheckResult.OK)
                        {
                            list.Add(new MyWelder.ProjectionRaycastData(BuildCheckResult.OK, block, projector));
                            break;
                        }
                    }
                }
            }

            entitiesInSphere.Clear();

            return list;
        }
    }
}