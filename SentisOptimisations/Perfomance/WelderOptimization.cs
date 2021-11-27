using System;
using System.Collections.Generic;
using System.Reflection;
using NAPI;
using ParallelTasks;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
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
    public class WelderOptimization
    {
        const string ActionNAME = "ShipWelder BuildProjection";


        public static Random random = new Random();

        public static void Patch(PatchContext ctx)
        {
            var MethodActivate = typeof(MyShipWelder).GetMethod
                ("Activate", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodActivate).Prefixes.Add(
                typeof(WelderOptimization).GetMethod(nameof(Activate),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool Activate(MyShipWelder __instance, ref bool __result, HashSet<MySlimBlock> targets)
        {
            __result = false; //it affects only sound;
            if (!SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.WelderTweaksEnabled)
            {
                return true;
            }

            if (SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.WelderTweaksExcludeNanobot)
            {
                var def = (MyShipWelderDefinition) __instance.BlockDefinition;
                if (def.SensorRadius < 0.01f) //NanobotOptimiztion
                {
                    return false;
                }
            }

            if (!SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.WelderTweaksSelfWelding)
            {
                targets.Remove(__instance.SlimBlock);
            }

            if (SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.WelderTweaksWeldNextFrames)
            {
                var ntargets = new HashSet<MySlimBlock>(targets.Count);
                foreach (var x in targets)
                {
                    ntargets.Add(x);
                }

                FrameExecutor.addDelayedLogic(random.Next(4) + 1, (x) => { ActivateInternal(__instance, ntargets); });
            }
            else
            {
                ActivateInternal(__instance, targets);
            }

            //we dont need any checks now;
            return false;
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
                }
            }

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
            
            if (SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.AsyncWeld)
            {
                var targetsToThread = new HashSet<MySlimBlock>(targets);
                Parallel.StartBackground(() => Weld(welder, targetsToThread, inventory, num, WeldProjectionsWithWelding));
                return;
            }

            var welded = Weld(welder, targets, inventory, num);

            WeldProjectionsWithWelding(welder, welded);
        }

        private static void WeldProjectionsWithWelding(MyShipWelder welder, bool welded)
        {
            if (!welded || SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config
                .WelderTweaksCanWeldProjectionsIfWeldedOtherBlocks)
            {
                if (ShipToolPatch.IsSuperWelder(welder) && random.Next(0, 10) > 1)
                {
                    return;
                }

                if (SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.WelderTweaksWeldProjectionsNextFrame)
                {
                    FrameExecutor.addDelayedLogic(random.Next(4), (x) => { WeldProjections(welder); });
                }
                else
                {
                    WeldProjections(welder);
                }
            }
        }


        public static bool Weld(MyShipWelder welder, HashSet<MySlimBlock> targets, MyInventory inventory,
            int foundBlocks, Action<MyShipWelder, bool> callback = null)
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
                    if (!SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.WelderTweaksNoLimitsCheck)
                    {
                        canWeld = true;
                    }
                    else
                    {
                        bool? flag2 = block.ComponentStack.WillFunctionalityRise(weldAmount);
                        if (flag2 == null || !flag2.Value || MySession.Static.CheckLimitsAndNotify(
                            MySession.Static.LocalPlayerId, block.BlockDefinition.BlockPairName,
                            block.BlockDefinition.PCU - MyCubeBlockDefinition.PCU_CONSTRUCTION_STAGE_COST, 0, 0, null))
                        {
                            canWeld = true;
                        }
                    }

                    if (canWeld)
                    {
                        MyAPIGateway.Utilities.InvokeOnGameThread(() => Action(block));
                        void Action(MySlimBlock blockToWeld)
                        {
                            try
                            {
                                blockToWeld.MoveItemsToConstructionStockpile(inventory);
                                blockToWeld.MoveUnneededItemsFromConstructionStockpile(inventory);
                                if (blockToWeld.HasDeformation || blockToWeld.MaxDeformation > 0.0001f || !blockToWeld.IsFullIntegrity)
                                {
                                    blockToWeld.IncreaseMountLevel(weldAmount, welder.OwnerId, inventory, maxAllowedBoneMovement, welder.HelpOthers, welder.IDModule.ShareMode, false, false);

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

            if (SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.AsyncWeld)
            {
                try
                {
                    callback?.Invoke(welder, weldedAnyThing);
                }
                catch (Exception e)
                {
                    //...
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


            if (SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.AsyncWeld)
            {
                Parallel.StartBackground(() => FindProjectedBlocks(welder, DoWeldProjections));
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
            if (!SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.WelderSkipCreativeWelding)
            {
                flag3 = MySession.Static.CreativeMode;
                MyPlayer.PlayerId id2;
                MyPlayer myPlayer;
                if (MySession.Static.Players.TryGetPlayerId(welder.BuiltBy, out id2) &&
                    MySession.Static.Players.TryGetPlayerById(id2, out myPlayer))
                {
                    flag3 |= MySession.Static.CreativeToolsEnabled(Sync.MyId);
                }
            }

            foreach (MyWelder.ProjectionRaycastData projectionRaycastData in array)
            {
                if (welder.IsWithinWorldLimits(projectionRaycastData.cubeProjector,
                    projectionRaycastData.hitCube.BlockDefinition.BlockPairName,
                    flag3
                        ? projectionRaycastData.hitCube.BlockDefinition.PCU
                        : MyCubeBlockDefinition.PCU_CONSTRUCTION_STAGE_COST) && (MySession.Static.CreativeMode ||
                    inventory.ContainItems(new MyFixedPoint?(1),
                        projectionRaycastData.hitCube
                            .BlockDefinition.Components[0]
                            .Definition.Id, MyItemFlags.None)))
                {
                    MyWelder.ProjectionRaycastData invokedBlock = projectionRaycastData;

                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    {
                        try
                        {
                            MySandboxGame.Static.Invoke((Action) (() =>
                            {
                                if (invokedBlock.cubeProjector.Closed || invokedBlock.cubeProjector.CubeGrid.Closed ||
                                    invokedBlock.hitCube.FatBlock != null && invokedBlock.hitCube.FatBlock.Closed)
                                    return;
                                invokedBlock.cubeProjector.Build(invokedBlock.hitCube, welder.OwnerId, welder.EntityId,
                                    builtBy: welder.BuiltBy);
                            }), "ShipWelder BuildProjection");
                        }
                        catch (Exception e)
                        {
                            SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e);
                        }
                    });
                }
            }
        }

        // Token: 0x060019BA RID: 6586 RVA: 0x0007BD0C File Offset: 0x00079F0C
        private static List<MyWelder.ProjectionRaycastData> FindProjectedBlocks(MyShipWelder welder,
            Action<MyShipWelder, List<MyWelder.ProjectionRaycastData>> callback = null)
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
                    if (!SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.AsyncWeld)
                    {
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
                    }
                    else
                    {
                        HashSet<MySlimBlock> m_projectedBlockForThread = new HashSet<MySlimBlock>(m_projectedBlock);
                        MyAPIGateway.Utilities.InvokeOnGameThread(() => Action(m_projectedBlockForThread));

                        void Action(HashSet<MySlimBlock> blocksInThread)
                        {
                            foreach (MySlimBlock mySlimBlock in blocksInThread)
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

                            try
                            {
                                callback?.Invoke(welder, list);
                            }
                            catch (Exception e)
                            {
                                //...
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