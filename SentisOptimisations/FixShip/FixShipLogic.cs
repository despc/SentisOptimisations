using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisOptimisations
{
    public class FixShipLogic
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static void FixGroups(List<MyCubeGrid> groups)
        {
            FixGroup(groups);
        }

        public static void FixGroupByGrid(MyCubeGrid grid)
        {
            FixGroups(FindGroupByGrid(grid));
        }
        
        public static List<MyCubeGrid> FindGroupByGrid(MyCubeGrid grid)
        {
            List<MyCubeGrid> groupNodes =
                MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Physical).GetGroupNodes(grid);
            return groupNodes;
        }

        private static void FixGroup(List<MyCubeGrid> groups)
        {
            string str = "Server";

            List<MyObjectBuilder_EntityBase> objectBuilders = new List<MyObjectBuilder_EntityBase>();
            List<MyCubeGrid> myCubeGridList = new List<MyCubeGrid>();
            Dictionary<Vector3I, MyCharacter> characters = new Dictionary<Vector3I, MyCharacter>();

            foreach (MyCubeGrid nodeData in groups)
            {
                myCubeGridList.Add(nodeData);
                // nodeData.Physics.LinearVelocity = Vector3.Zero;
                MyObjectBuilder_EntityBase objectBuilder = nodeData.GetObjectBuilder(true);
                if (!objectBuilders.Contains(objectBuilder))
                {
                    objectBuilders.Add(objectBuilder);
                }

                foreach (var myCubeBlock in nodeData.GetFatBlocks())
                {
                    if (myCubeBlock is MyCockpit)
                    {
                        var cockpit = (MyCockpit) myCubeBlock;
                        MyCharacter myCharacter = cockpit.Pilot;
                        if (myCharacter == null)
                        {
                            continue;
                        }
                        characters[cockpit.Position] = myCharacter;
                        cockpit.RemovePilot();
                    }
                }
            }

            foreach (MyCubeGrid myCubeGrid in myCubeGridList)
            {
                IMyEntity myEntity = (IMyEntity) myCubeGrid;
                Log.Warn("Auto fixship after convert Grid " +
                         myCubeGrid.DisplayName );

                myEntity.Close();
            }

            MyAPIGateway.Entities.RemapObjectBuilderCollection(
                (IEnumerable<MyObjectBuilder_EntityBase>) objectBuilders);
            foreach (MyObjectBuilder_EntityBase cubeGrid in objectBuilders)
                
                MyAPIGateway.Entities.CreateFromObjectBuilderParallel(cubeGrid,
                    completionCallback: ((Action<IMyEntity>) (entity =>
                    {
                        ((MyCubeGrid) entity).DetectDisconnectsAfterFrame();
                        MyAPIGateway.Entities.AddEntity(entity);
                    })));
        }
    }
}