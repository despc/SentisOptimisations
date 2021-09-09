using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using Sandbox.Engine.Networking;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Weapons;
using Sandbox.ModAPI;
using Torch.Commands;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.Utils;

namespace FixTurrets.Garage
{
    
    public class GarageCore
    {
        
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static GarageCore Instance = new GarageCore();
        
        public bool MoveToGarage(long identityId, MyCubeGrid myCubeGrid, CommandContext context = null)
        {
            if (!myCubeGrid.BigOwners.Contains(identityId))
            {
                context?.Respond("Сохранить структуру может только владелец " + myCubeGrid.DisplayName);
                return true;
            }

            context?.Respond("Сохраняем структуру " + myCubeGrid.DisplayName);
            var pathToGarage = SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.PathToGarage;

            List<MyCubeGrid> grids = new List<MyCubeGrid>();
            grids.Add(myCubeGrid);
            int totalpcu = 0;
            int totalblocks = 0;
            List<MyObjectBuilder_CubeGrid> gridsOB = new List<MyObjectBuilder_CubeGrid>();
            foreach (MyCubeGrid сubeGrid in grids)
            {
                try
                {
                    foreach (MyCubeBlock fatBlock in сubeGrid.GetFatBlocks())
                    {
                        MyCubeBlock c = fatBlock;
                        if (c is MyCockpit)
                            (c as MyCockpit).RemovePilot();
                        if (c is MyProgrammableBlock)
                        {
                            try
                            {
                                SentisOptimisationsPlugin.SentisOptimisationsPlugin.m_myProgrammableBlockKillProgramm.Invoke(
                                    (object) (c as MyProgrammableBlock), new object[1]
                                    {
                                        (object) MyProgrammableBlock.ScriptTerminationReason.None
                                    });
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "MyProgrammableBlock hack eval");
                            }
                        }

                        if (c is MyShipDrill)
                            (c as MyShipDrill).Enabled = false;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "(SaveGrid)Exception in block disables ex");
                }

                totalpcu += сubeGrid.BlocksPCU;
                totalblocks += сubeGrid.BlocksCount;
                MyObjectBuilder_CubeGrid objectBuilder =
                    (MyObjectBuilder_CubeGrid) сubeGrid.GetObjectBuilder(true);
                gridsOB.Add(objectBuilder);
            }

            string gridName = gridsOB[0].DisplayName.Length <= 30
                ? gridsOB[0].DisplayName
                : gridsOB[0].DisplayName.Substring(0, 30);
            string filenameexported = DateTime.Now.ToLongTimeString()
                                      + "_" + (object) totalpcu + "_" + (object) totalblocks + "_" + gridName;

            MyObjectBuilder_ShipBlueprintDefinition newObject1 =
                MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_ShipBlueprintDefinition>();
            newObject1.Id = (SerializableDefinitionId) new MyDefinitionId(
                new MyObjectBuilderType(typeof(MyObjectBuilder_ShipBlueprintDefinition)),
                MyUtils.StripInvalidChars(filenameexported));
            newObject1.CubeGrids = gridsOB.ToArray();
            newObject1.RespawnShip = false;
            newObject1.DisplayName = MyGameService.UserName;
            newObject1.OwnerSteamId = Sync.MyId;
            newObject1.CubeGrids[0].DisplayName = myCubeGrid.DisplayName;
            MyObjectBuilder_Definitions newObject2 =
                MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Definitions>();
            newObject2.ShipBlueprints = new MyObjectBuilder_ShipBlueprintDefinition[1];
            newObject2.ShipBlueprints[0] = newObject1;

            string str = Path.Combine(pathToGarage, MyAPIGateway.Players.TryGetSteamId(identityId).ToString());
            if (!Directory.Exists(str))
                Directory.CreateDirectory(str);
            foreach (char ch in ((IEnumerable<char>) Path.GetInvalidPathChars()).Concat<char>(
                (IEnumerable<char>) Path.GetInvalidFileNameChars()))
                filenameexported = filenameexported.Replace(ch.ToString(), ".");
            string path = Path.Combine(str, filenameexported + ".sbc");
            bool flag = MyObjectBuilderSerializer.SerializeXML(path, false, (MyObjectBuilder_Base) newObject2);
            if (flag)
                MyObjectBuilderSerializer.SerializePB(path + "B5", true, (MyObjectBuilder_Base) newObject2);
            MyAPIGateway.Utilities.InvokeOnGameThread((Action) (() =>
            {
                foreach (MyEntity myEntity in grids)
                    myEntity.Close();
            }));
            context?.Respond("Cтруктура " + myCubeGrid.DisplayName + " сохранена в гараж");
            return false;
        }
    }
}