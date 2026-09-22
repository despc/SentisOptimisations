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
using Sandbox.Game.SessionComponents;
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
        /// <summary>
        /// How many new projector CanBuild checks one welder activation makes; the next activation
        /// goes on where this one stopped, and the neighbours of a block just built come first.
        /// </summary>
        private const int ProjectionChecksPerActivation = 24;

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

            /// <summary>
            /// Half the diagonal of each block, in metres. A welder reaches a block when its sensor
            /// touches the block, not when it touches the block's centre: a jump drive or a safe
            /// zone is three cells across, and measuring to the middle puts it out of reach while
            /// the tool is sitting right against its face.
            /// </summary>
            public readonly List<float> Extents = new List<float>();

            public readonly ProjectionFrontierCursor Cursor = new ProjectionFrontierCursor();
            public readonly Queue<Vector3I> Ready = new Queue<Vector3I>();
            public readonly HashSet<Vector3I> Queued = new HashSet<Vector3I>();
            public long LastSeenFrame;

            public void Reset(MyCubeGrid preview)
            {
                Preview = preview;
                Ready.Clear();
                Queued.Clear();
                Compact();
            }

            /// <summary>The cells still to build, without the ones built since; the queue stays.</summary>
            public void Compact()
            {
                Positions.Clear();
                Extents.Clear();
                if (Preview != null)
                    foreach (var block in Preview.CubeBlocks)
                    {
                        Positions.Add(block.Position);
                        Extents.Add(HalfDiagonal(block, Preview.GridSize));
                    }

                Cursor.Reset();
            }

            private static float HalfDiagonal(MySlimBlock block, float gridSize)
            {
                var cells = block.Max - block.Min + Vector3I.One;
                return 0.5f * gridSize * new Vector3(cells.X, cells.Y, cells.Z).Length();
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

            WelderDiagnostics.Count(ref WelderDiagnostics.Activations);
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
        private static readonly List<MyEntity> GridsInSphere = new List<MyEntity>();
        private static readonly Dictionary<MyDefinitionId, int> ComponentsToPull = new Dictionary<MyDefinitionId, int>();
        /// <summary>
        /// The cells of a projection one welder can reach, and where it stopped testing them.
        ///
        /// Which cells are in reach only changes when the ship moves or the blueprint does, and
        /// working it out means walking every cell of the projection: on a large blueprint, with a
        /// wall of welders asking four times a second each, that is hundreds of thousands of
        /// distance checks a second for an answer that did not change. So it is kept until the tool
        /// has moved <see cref="ReachMovedM"/>, the projection has gained or lost a block, or
        /// <see cref="ReachFrames"/> have passed.
        /// </summary>
        private sealed class Reach
        {
            public readonly List<Vector3I> Cells = new List<Vector3I>();
            public readonly ProjectionFrontierCursor Cursor = new ProjectionFrontierCursor();
            public Vector3D Center;
            public double Radius;
            public int PreviewBlocks;
            public long Frame = long.MinValue;
            public long Projector;
        }

        private const double ReachMovedM = 0.5;
        private const long ReachFrames = 60;

        private static readonly Dictionary<long, Reach> Reaches = new Dictionary<long, Reach>();

        private static Reach ReachOf(long welderId)
        {
            Reach reach;
            if (Reaches.TryGetValue(welderId, out reach)) return reach;
            if (Reaches.Count > 512) Reaches.Clear();   // tools come and go; this is only a cache
            reach = new Reach();
            Reaches[welderId] = reach;
            return reach;
        }

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
                var held = (int) inventory.GetItemAmount(myDefinitionId, MyItemFlags.None, false);
                if (Math.Max(keyValuePair.Value - held, 0) == 0 || !welder.UseConveyorSystem) continue;

                // Queued pulls are worked off by the conveyor system later, which is fine while the
                // tool still holds some of the component - it welds from what it has and the top-up
                // catches up. Holding none of it is different: the tool welds nothing, so nothing
                // on the ship moves, and a queue that never gets worked off leaves it starved with
                // a full container behind it. That one is fetched now.
                var immediately = held == 0;
                welder.CubeGrid.GridSystems.ConveyorSystem.PullItem(myDefinitionId,
                    new MyFixedPoint?(keyValuePair.Value), welder, inventory, false, immediately);
            }

            m_missingComponents.Clear();

            // AsyncWeld v2: the weld itself (IncreaseMountLevel, inventory, conveyors, networking)
            // must run on the game thread; it already does here. Queuing it on a worker only
            // created data races, so the inline path is the only path.
            var welded = Weld(welder, targets, inventory, num);
            if (welded) WelderDiagnostics.Count(ref WelderDiagnostics.Welded);

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
            // spent the global materialization budget for this simulation frame - but do not drop
            // the turn either. Welders of one ship activate in the same frame, a quarter of a
            // second apart, so with one build a frame the second and the third tool of a ship lost
            // every turn they had: three welders built as fast as one. They wait for the next
            // frame with room instead (see RunDeferred), which keeps the frame's cost where the
            // cap puts it and the ship's speed where its welders put it.
            if (!ProjectionBudget.CanConsume(MySession.Static.GameplayFrameCounter,
                    SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.ProjectionBuildsPerFrame,
                    welder.EntityId))
            {
                WelderDiagnostics.Count(ref WelderDiagnostics.NoPermit);
                if (DeferredIds.Add(welder.EntityId)) Deferred.Enqueue(welder);
                return;
            }

            ScanAndBuild(welder);
        }

        private static readonly Queue<MyShipWelder> Deferred = new Queue<MyShipWelder>();
        private static readonly HashSet<long> DeferredIds = new HashSet<long>();

        /// <summary>
        /// Once a frame, from the plugin's update: the welders that were turned away by the frame
        /// budget get their scan now, while this frame still has room. No more scans than the
        /// budget has builds, so a queue of tools that find nothing cannot turn into a long frame.
        /// </summary>
        public static void RunDeferred()
        {
            if (Deferred.Count == 0) return;
            try
            {
                var config = SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config;
                var frame = MySession.Static.GameplayFrameCounter;
                var limit = Math.Max(1, config.ProjectionBuildsPerFrame);
                var scans = 0;
                while (Deferred.Count > 0 && scans < limit)
                {
                    var welder = Deferred.Peek();
                    if (!ProjectionBudget.CanConsume(frame, limit, welder.EntityId)) break;
                    Deferred.Dequeue();
                    DeferredIds.Remove(welder.EntityId);
                    if (!config.WelderTweaksEnabled || welder.MarkedForClose || welder.Closed || !welder.IsWorking)
                        continue;
                    scans++;
                    WelderDiagnostics.Count(ref WelderDiagnostics.DeferredScans);
                    ScanAndBuild(welder);
                }
            }
            catch (Exception e)
            {
                Deferred.Clear();
                DeferredIds.Clear();
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e);
            }
        }

        private static void ScanAndBuild(MyShipWelder welder)
        {
            WelderDiagnostics.Count(ref WelderDiagnostics.Scans);
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
                // A projector materializes a block out of the first component in its list, and the
                // welder has to be holding one. The queued form of PullItem - the one vanilla uses
                // here - is a request the conveyor system works off later, and on a tool that holds
                // none of that component there is no later: nothing is built, so nothing else ever
                // moves, and the ship sits over the block with a full container behind it. Measured
                // on the mixed slab: a queued pull delivered 0, an immediate one delivered at once.
                //
                // So the cheap queued pull tops the tool up as before, and a component the tool does
                // not have at all is fetched immediately. That path costs a synchronous conveyor
                // lookup, and it only runs for a component the welder is completely out of.
                var componentsToPull = ComponentsToPull;
                componentsToPull.Clear();
                foreach (var candidate in array)
                {
                    var candidateComponents = candidate.hitCube.BlockDefinition.Components;
                    if (candidateComponents == null || candidateComponents.Length == 0) continue;
                    componentsToPull.Sum(candidateComponents[0].Definition.Id, 1);
                }
                foreach (var component in componentsToPull)
                {
                    var immediately = !inventory.ContainItems(new MyFixedPoint?(1), component.Key, MyItemFlags.None);
                    WelderDiagnostics.Count(ref (immediately
                        ? ref WelderDiagnostics.PullsImmediate
                        : ref WelderDiagnostics.PullsQueued));
                    welder.CubeGrid.GridSystems.ConveyorSystem.PullItem(component.Key,
                        new MyFixedPoint?(Math.Min(component.Value, 8)), welder, inventory, false, immediately);
                }
            }

            foreach (MyWelder.ProjectionRaycastData projectionRaycastData in array)
            {
                var components = projectionRaycastData.hitCube.BlockDefinition.Components;
                if (components == null || components.Length == 0)
                    continue;

                var componentId = components[0].Definition.Id;
                WelderDiagnostics.Count(ref WelderDiagnostics.Candidates);
                if (!welder.IsWithinWorldLimits(projectionRaycastData.cubeProjector,
                        projectionRaycastData.hitCube.BlockDefinition.BlockPairName,
                        projectionRaycastData.hitCube.BlockDefinition.PCU))
                {
                    WelderDiagnostics.Count(ref WelderDiagnostics.OverLimits);
                    continue;
                }

                if (!MySession.Static.CreativeMode &&
                    !inventory.ContainItems(new MyFixedPoint?(1), componentId, MyItemFlags.None))
                {
                    WelderDiagnostics.Count(ref WelderDiagnostics.NoComponents);
                    continue;
                }

                // This is deliberately global, not per welder: multiple tools are updated in
                // one simulation frame and their synchronous Build costs otherwise stack.
                if (!ProjectionBudget.TryConsume(MySession.Static.GameplayFrameCounter,
                        SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.ProjectionBuildsPerFrame,
                        welder.EntityId))
                {
                    WelderDiagnostics.Count(ref WelderDiagnostics.BudgetSpent);
                    break;
                }

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
                    WelderDiagnostics.Count(ref WelderDiagnostics.Builds);
                    QueueProjectedNeighbors(invokedBlock.cubeProjector, invokedBlock.hitCube);
                }
                catch (Exception e)
                {
                    SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e);
                }
            }
        }

        /// <summary>
        /// A block that was just built is what the blocks against it can now be built from, so
        /// they go to the front of the search. That means every block touching any of its faces,
        /// not only the six cells around its centre: a thruster or a turret is several cells
        /// across, and on a real ship most of what grows out of it touches it somewhere else.
        /// </summary>
        private static void QueueProjectedNeighbors(MyProjectorBase projector, MySlimBlock built)
        {
            var preview = projector?.ProjectedGrid;
            if (preview == null || built == null) return;
            ProjectionFrontierState state;
            if (!ProjectionFrontiers.TryGetValue(projector.EntityId, out state)) return;
            var min = built.Min;
            var max = built.Max;
            foreach (var direction in NeighborDirections)
            {
                // the layer of cells just outside this face
                var from = min;
                var to = max;
                if (direction.X > 0) from.X = to.X = max.X + 1;
                else if (direction.X < 0) from.X = to.X = min.X - 1;
                else if (direction.Y > 0) from.Y = to.Y = max.Y + 1;
                else if (direction.Y < 0) from.Y = to.Y = min.Y - 1;
                else if (direction.Z > 0) from.Z = to.Z = max.Z + 1;
                else from.Z = to.Z = min.Z - 1;
                for (var x = from.X; x <= to.X; x++)
                for (var y = from.Y; y <= to.Y; y++)
                for (var z = from.Z; z <= to.Z; z++)
                {
                    var neighbour = preview.GetCubeBlock(new Vector3I(x, y, z));
                    if (neighbour != null) state.Enqueue(neighbour.Position);
                }
            }
        }

        /// <summary>
        /// Whether the projector will really build this block for this welder. A block from a DLC
        /// the welder's owner does not have passes CanBuild, and the projector then quietly builds
        /// nothing - and tells whoever flies the ship about the missing DLC, every time. A welder
        /// handed one of those spent its turn and the frame's build on it for ever: on a real ship
        /// with a few DLC lights and panels the last blocks never came, and nothing else did either.
        /// </summary>
        private static bool OwnerHasDlc(MySlimBlock block, ulong ownerSteamId)
        {
            var dlc = MySession.Static.GetComponent<MySessionComponentDLC>();
            if (dlc == null || dlc.GetFirstMissingDefinitionDLC(block.BlockDefinition, ownerSteamId) == null) return true;
            WelderDiagnostics.Count(ref WelderDiagnostics.NoDlc);
            return false;
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
            // A build takes one block out of the preview, and that must not throw away what the
            // search has learnt: it used to, on every build, and the queue of blocks next to fresh
            // ones was emptied before anyone got to it - on a real ship the welders were left
            // sweeping the whole blueprint blind. Built cells just drop out as they are met; the
            // lists are only compacted once a good share of them is gone. A preview that grew is a
            // different blueprint, and starts over.
            if (state.Preview != preview || preview.BlocksCount > state.Positions.Count)
                state.Reset(preview);
            else if (preview.BlocksCount < state.Positions.Count * 9 / 10)
                state.Compact();
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
            // Only grids can carry a projection, and top-level ones at that: the full query also
            // collected every functional block of every grid in the sphere, which for a big radius
            // over a big ship was thousands of entities for nothing. The game's list is shared, so
            // it is copied out before anything else can run a query into it.
            var found = MyEntities.GetTopMostEntitiesInSphere(ref boundingSphereD);
            var entitiesInSphere = GridsInSphere;
            entitiesInSphere.Clear();
            foreach (var entity in found)
                if (entity is MyCubeGrid) entitiesInSphere.Add(entity);
            found.Clear();
            var frame = MySession.Static.GameplayFrameCounter;
            var checks = ProjectionChecksPerActivation;
            var ownerSteamId = MySession.Static.Players.TryGetSteamId(welder.OwnerId);

            foreach (MyEntity myEntity in entitiesInSphere)
            {
                MyCubeGrid myCubeGrid = myEntity as MyCubeGrid;
                if (myCubeGrid != null && myCubeGrid.Projector != null)
                {
                    var projector = myCubeGrid.Projector;
                    var state = FrontierState(projector, myCubeGrid, frame);

                    // Validate a few high-value frontier cells first. Stale entries are cheap and
                    // bounded; successful builds enqueue only their six grid neighbours.
                    // Only CanBuild calls count against the allowance: an entry whose block has been
                    // built since costs one lookup and is simply dropped. A block out of this tool's
                    // reach goes back for the others, and a block that passes stays queued until it
                    // is really built - this frame's build may go to another tool, or the component
                    // may still be on its way, and a candidate lost from the queue is left to the
                    // blind sweep below, which on a big blueprint takes a long time to come back.
                    var readyChecks = 4;
                    var readyLeft = state.Ready.Count;
                    while (readyChecks > 0 && readyLeft-- > 0 && list.Count == 0)
                    {
                        var position = state.Ready.Dequeue();
                        state.Queued.Remove(position);
                        var block = myCubeGrid.GetCubeBlock(position);
                        if (block == null) continue;
                        if (!block.WorldAABB.Intersects(boundingSphereD))
                        {
                            state.Enqueue(position);
                            continue;
                        }

                        readyChecks--;
                        if (projector.CanBuild(block, true) == BuildCheckResult.OK && OwnerHasDlc(block, ownerSteamId))
                        {
                            list.Add(new MyWelder.ProjectionRaycastData(BuildCheckResult.OK, block, projector));
                            state.Enqueue(position);
                        }
                    }

                    if (list.Count != 0) continue;

                    // Only the cells this welder could actually build are worth a CanBuild call -
                    // the expensive part - and the cursor has to walk those, not the whole
                    // projection. Sharing one cursor over every cell of the blueprint left a welder
                    // spending its whole allowance on cells metres outside its own sensor: on the
                    // mixed slab the tools saw two candidates between twenty-two of them where
                    // vanilla saw fifty-four, and the ship sat over buildable blocks doing nothing.
                    var reach = ReachOf(welder.EntityId);
                    if (reach.Projector != projector.EntityId ||
                        reach.PreviewBlocks != state.Positions.Count ||
                        reach.Radius != boundingSphereD.Radius ||
                        frame - reach.Frame > ReachFrames ||
                        Vector3D.DistanceSquared(reach.Center, boundingSphereD.Center) > ReachMovedM * ReachMovedM)
                    {
                        reach.Cells.Clear();
                        for (var i = 0; i < state.Positions.Count; i++)
                        {
                            var position = state.Positions[i];
                            // The sensor has to touch the block, not its centre: a three-cell block
                            // whose face is right against the tool would otherwise never be found.
                            var extent = i < state.Extents.Count ? state.Extents[i] : 0f;
                            var reachable = boundingSphereD.Radius + extent;
                            if (Vector3D.DistanceSquared(myCubeGrid.GridIntegerToWorld(position),
                                    boundingSphereD.Center) > reachable * reachable) continue;
                            reach.Cells.Add(position);
                        }

                        reach.Center = boundingSphereD.Center;
                        reach.Radius = boundingSphereD.Radius;
                        reach.PreviewBlocks = state.Positions.Count;
                        reach.Projector = projector.EntityId;
                        reach.Frame = frame;
                    }

                    if (reach.Cells.Count == 0) continue;

                    // Each welder keeps its own place in that list, so tools sitting side by side
                    // do not all test the same few cells.
                    var start = reach.Cursor.Take(reach.Cells.Count, Math.Min(checks, reach.Cells.Count));
                    foreach (var index in start)
                    {
                        var block = myCubeGrid.GetCubeBlock(reach.Cells[index]);
                        if (block != null && projector.CanBuild(block, true) == BuildCheckResult.OK &&
                            OwnerHasDlc(block, ownerSteamId))
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