using System;
using System.Collections.Generic;
using System.Reflection;
using NAPI;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
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


        public static void ActivateInternal(MyShipWelder welder, HashSet<MySlimBlock> targets)
        {
            Dictionary<string, int> m_missingComponents = new Dictionary<string, int>();
            if (welder.MarkedForClose || welder.Closed)
            {
                return;
            }

            int num = targets.Count;
            m_missingComponents.Clear();
            HashSet<MySlimBlock> targetsToWeld = new HashSet<MySlimBlock>();
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



            var array = FindProjectedBlocks(welder);
            DoWeldProjections(welder, array);
        }

        private static void DoWeldProjections(MyShipWelder welder, List<MyWelder.ProjectionRaycastData> array)
        {
            MyInventory inventory = welder.GetInventory(0);
            if (welder.UseConveyorSystem)
            {
                Dictionary<MyDefinitionId, int> componentsToPull = new Dictionary<MyDefinitionId, int>();
                for (int i = 0; i < array.Count; i++)
                {
                    MyCubeBlockDefinition.Component[] components = array[i].hitCube.BlockDefinition.Components;
                    if (components != null && components.Length != 0)
                    {
                        MyDefinitionId id = components[0].Definition.Id;
                        componentsToPull.Sum(id, 1);
                    }
                }

                foreach (var x in componentsToPull)
                {
                    welder.CubeGrid.GridSystems.ConveyorSystem.PullItem(x.Key, new MyFixedPoint?(x.Value), welder,
                        inventory,
                        false, false);
                }
            }

            bool flag3 = false;

            foreach (MyWelder.ProjectionRaycastData projectionRaycastData in array)
            {
                if (welder.IsWithinWorldLimits(projectionRaycastData.cubeProjector,
                        projectionRaycastData.hitCube.BlockDefinition.BlockPairName,
                        projectionRaycastData.hitCube.BlockDefinition.PCU)
                    && (MySession.Static.CreativeMode ||
                        inventory.ContainItems(new MyFixedPoint?(1),
                            projectionRaycastData.hitCube
                                .BlockDefinition.Components[0]
                                .Definition.Id, MyItemFlags.None)))
                {
                    // game thread already: build the projection inline
                    MyWelder.ProjectionRaycastData invokedBlock = projectionRaycastData;
                    try
                    {
                    if (invokedBlock.cubeProjector.Closed ||
                        invokedBlock.cubeProjector.CubeGrid.Closed ||
                        invokedBlock.hitCube.FatBlock != null && invokedBlock.hitCube.FatBlock.Closed)
                        return;
                    invokedBlock.cubeProjector.Build(invokedBlock.hitCube, welder.OwnerId,
                        welder.EntityId,
                        builtBy: welder.BuiltBy);
                    }
                    catch (Exception e)
                    {
                        SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e);
                    }
                }
            }
        }

        private static List<MyWelder.ProjectionRaycastData> FindProjectedBlocks(MyShipWelder welder)
        {
            HashSet<MySlimBlock> m_projectedBlock = new HashSet<MySlimBlock>();
            var w = welder.WorldMatrix;
            var d = (MyShipWelderDefinition) (welder.BlockDefinition);
            BoundingSphereD boundingSphereD = new BoundingSphereD(
                w.Translation + w.Forward * (welder.CubeGrid.GridSize * 1.5f + d.SensorOffset),
                ShipToolPatch.GetWelderRadius(welder));
            List<MyWelder.ProjectionRaycastData> list = new List<MyWelder.ProjectionRaycastData>();
            List<MyEntity> entitiesInSphere = MyEntities.GetEntitiesInSphere(ref boundingSphereD);

            foreach (MyEntity myEntity in entitiesInSphere)
            {
                MyCubeGrid myCubeGrid = myEntity as MyCubeGrid;
                if (myCubeGrid != null && myCubeGrid.Projector != null)
                {
                    myCubeGrid.GetBlocksInsideSphere(ref boundingSphereD, m_projectedBlock, false);
                    foreach (MySlimBlock mySlimBlock in m_projectedBlock)
                    {
                        if (myCubeGrid.Projector.CanBuild(mySlimBlock, true) == BuildCheckResult.OK)
                        {
                            MySlimBlock cubeBlock = myCubeGrid.GetCubeBlock(mySlimBlock.Position);
                            if (cubeBlock != null)
                            {
                                list.Add(new MyWelder.ProjectionRaycastData(BuildCheckResult.OK, cubeBlock,
                                    myCubeGrid.Projector));
                            }
                        }
                    }

                    m_projectedBlock.Clear();
                }
            }

            m_projectedBlock.Clear();
            entitiesInSphere.Clear();

            return list;
        }
    }
}