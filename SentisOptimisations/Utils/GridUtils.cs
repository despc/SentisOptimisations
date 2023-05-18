// Decompiled with JetBrains decompiler
// Type: Torch.SharedLibrary.Utils.GridUtils
// Assembly: SKO-GridPCULimiter, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 763A9EB8-F42B-4B0E-ACDE-740B34BF1092
// Assembly location: C:\Users\idesp\Downloads\sko-gridpculimiter-v1.2.1\SKO-GridPCULimiter.dll

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Groups;
using VRage.ModAPI;
using VRageMath;

namespace SentisOptimisations
{
    public static class GridUtils
    {
        public static int GetPCU(IMyCubeGrid grid, bool includeSubGrids = false, bool includeConnectorDocked = false)
        {
            int num = 0;
            if (grid != null && grid.Physics != null)
            {
                List<IMySlimBlock> blocks = new List<IMySlimBlock>();
                grid.GetBlocks(blocks);
                bool hasActiveEngine = false;
                foreach (IMySlimBlock mySlimBlock in blocks)
                {
                    num += BlockUtils.GetPCU(mySlimBlock as MySlimBlock);
                }
               
                if (includeSubGrids)
                {
                    List<IMyCubeGrid> subGrids = GetSubGrids(grid, includeConnectorDocked);
                    if (subGrids != null)
                    {
                        foreach (IMyCubeGrid grid1 in subGrids)
                            num += GetPCU(grid1);
                    }
                }
            }

            return num;
        }

        public static List<IMyCubeGrid> GetSubGrids(
            IMyCubeGrid grid,
            bool includeConnectorDocked = false)
        {
            List<IMyCubeGrid> source = new List<IMyCubeGrid>();
            if (grid == null || grid.Physics == null)
                return source;
            GridLinkTypeEnum type = GridLinkTypeEnum.Mechanical;
            if (includeConnectorDocked)
                type = GridLinkTypeEnum.Electrical;
            MyAPIGateway.GridGroups.GetGroup(((IMyCubeGrid)grid), type, (ICollection<IMyCubeGrid>) source);
            if (source != null && source.Count > 0)
            {
                IMyCubeGrid myCubeGrid = source
                    .Where<IMyCubeGrid>((Func<IMyCubeGrid, bool>) (c =>
                        ((IMyEntity) c).EntityId == ((IMyEntity) grid).EntityId)).FirstOrDefault<IMyCubeGrid>();
                if (myCubeGrid != null)
                    source.Remove(myCubeGrid);
            }

            return source;
        }

        public static List<IMySlimBlock> GetBlocks<T>(IMyCubeGrid grid)
        {
            List<IMySlimBlock> blocks = new List<IMySlimBlock>();
            if (grid != null && grid.Physics != null)
                grid.GetBlocks(blocks, (Func<IMySlimBlock, bool>) (b =>
                {
                    if (b.FatBlock == null)
                        return false;
                    return b is T || b.FatBlock is T;
                }));
            return blocks;
        }

        public static void SetBlocksEnabled<T>(IMyCubeGrid grid, bool enabled, bool update = false)
        {
            foreach (IMySlimBlock block in GetBlocks<T>(grid))
            {
                if (block.FatBlock is IMyFunctionalBlock)
                    (block as IMyFunctionalBlock).Enabled = enabled;
            }

            if (!update)
                return;
            (grid as MyCubeGrid).RaiseGridChanged();
        }

        public static ConcurrentBag<List<MyCubeGrid>> FindGridList(
            long playerId,
            bool includeConnectedGrids)
        {
            ConcurrentBag<List<MyCubeGrid>> grids = new ConcurrentBag<List<MyCubeGrid>>();
            if (includeConnectedGrids)
                Parallel.ForEach<MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group>(
                    (IEnumerable<MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group>) MyCubeGridGroups.Static.Physical
                        .Groups, (Action<MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group>) (group =>
                    {
                        List<MyCubeGrid> gridList = new List<MyCubeGrid>();
                        foreach (MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Node node in group.Nodes)
                        {
                            MyCubeGrid nodeData = node.NodeData;
                            if (nodeData.Physics != null)
                                gridList.Add(nodeData);
                        }

                        if (!IsPlayerIdCorrect(playerId, gridList))
                            return;
                        grids.Add(gridList);
                    }));
            else
                Parallel.ForEach<MyGroups<MyCubeGrid, MyGridMechanicalGroupData>.Group>(
                    (IEnumerable<MyGroups<MyCubeGrid, MyGridMechanicalGroupData>.Group>) MyCubeGridGroups.Static
                        .Mechanical.Groups, (Action<MyGroups<MyCubeGrid, MyGridMechanicalGroupData>.Group>) (group =>
                    {
                        List<MyCubeGrid> gridList = new List<MyCubeGrid>();
                        foreach (MyGroups<MyCubeGrid, MyGridMechanicalGroupData>.Node node in group.Nodes)
                        {
                            MyCubeGrid nodeData = node.NodeData;
                            if (nodeData.Physics != null)
                                gridList.Add(nodeData);
                        }

                        if (!IsPlayerIdCorrect(playerId, gridList))
                            return;
                        grids.Add(gridList);
                    }));
            return grids;
        }

        private static bool IsPlayerIdCorrect(long playerId, List<MyCubeGrid> gridList)
        {
            MyCubeGrid myCubeGrid = (MyCubeGrid) null;
            foreach (MyCubeGrid grid in gridList)
            {
                if (myCubeGrid == null || myCubeGrid.BlocksCount < grid.BlocksCount)
                    myCubeGrid = grid;
            }

            if (myCubeGrid == null)
                return false;
            if ((uint) myCubeGrid.BigOwners.Count > 0U)
                return playerId == myCubeGrid.BigOwners[0];
            return (ulong) playerId <= 0UL;
        }
        
        public static List<MyCubeGrid> FindAllGridsInRadius(Vector3 center, float radius)
        {
            MyDynamicAABBTreeD m_dynamicObjectsTree =
                (MyDynamicAABBTreeD) ReflectionUtils.GetPrivateStaticField(typeof(MyGamePruningStructure),
                    "m_dynamicObjectsTree");
            MyDynamicAABBTreeD m_staticObjectsTree =
                (MyDynamicAABBTreeD) ReflectionUtils.GetPrivateStaticField(typeof(MyGamePruningStructure),
                    "m_staticObjectsTree");
            BoundingSphereD boundingSphere = new BoundingSphereD(center, radius);
            List<MyEntity> result = new List<MyEntity>();
            m_dynamicObjectsTree.OverlapAllBoundingSphere(ref boundingSphere, result);
            m_staticObjectsTree.OverlapAllBoundingSphere(ref boundingSphere, result);
            List<MyCubeGrid> grids = new List<MyCubeGrid>();
            foreach (var myEntity in result)
            {
                if (myEntity is MyCubeGrid)
                {
                    grids.Add((MyCubeGrid) myEntity);
                }
            }
            return grids;
        }

        public static bool isNpcGrid(this MyCubeGrid instance)
        {
            var identityId = PlayerUtils.GetOwner(instance);
            if (identityId == 0)
            {
                return false;
            }
            return MySession.Static.Players.IdentityIsNpc(identityId);
        }
    }
}