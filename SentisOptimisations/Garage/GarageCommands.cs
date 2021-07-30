using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using Sandbox.Engine.Networking;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Gui;
using Sandbox.Game.GUI;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations;
using Torch;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.Voxels;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;
using Delegate = Garage.Delegate;

namespace SentisOptimisationsPlugin
{
    [Category("g")]
    public class GarageCommands : CommandModule
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        [Command("save", ".", null)]
        [Permission(MyPromoteLevel.None)]
        public void Reload()
        {
            IMyPlayer player = this.Context.Player;
            if (player == null)
            {
                Context.Respond("Проиграл, попробуй ещё");
                return;
            }
            var players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players);
            long identityId = player.IdentityId;
            IMyCharacter character = player.Character;
            foreach (var myPlayer in players)
            {
                if (myPlayer.GetRelationTo(identityId) != MyRelationsBetweenPlayerAndBlock.Enemies)
                {
                    continue;
                }
                var distance = Vector3D.Distance(myPlayer.GetPosition(), character.GetPosition());
                if (distance < 10000)
                {
                    Context.Respond("Рядом враги, обернись.");
                    return;
                }
            }

            if (character == null)
            {
                Context.Respond("Проиграл, попробуй ещё");
            }

            try
            {
                SaveGrid(character, identityId);
            }
            catch (Exception e)
            {
                Log.Error(e);
            }


            //this.Context.Respond("Структура сохранена");
        }

        private void SaveGrid(IMyCharacter character, long identityId)
        {
            MatrixD headMatrix = character.GetHeadMatrix(true);
            Matrix matrix = (Matrix) headMatrix;
            Vector3D vector3D = (Vector3D) (matrix.Translation + matrix.Forward * 0.5f);
            Vector3D worldEnd = (Vector3D) (matrix.Translation + matrix.Forward * 5000.5f);
            List<MyPhysics.HitInfo> mRaycastResult = new List<MyPhysics.HitInfo>();
            MyPhysics.CastRay(vector3D, worldEnd, mRaycastResult, 15);
            foreach (var hitInfo in new HashSet<MyPhysics.HitInfo>(mRaycastResult))
            {
                var hitEntity = hitInfo.HkHitInfo.GetHitEntity();
                if (hitEntity is MyCubeGrid)
                {
                    var myCubeGrid = ((MyCubeGrid) hitEntity);
                    if (!myCubeGrid.BigOwners.Contains(identityId))
                    {
                        this.Context.Respond("Сохранить структуру может только владелец " + hitEntity.DisplayName);
                        return;
                    }

                    this.Context.Respond("Сохраняем структуру " + hitEntity.DisplayName);
                    var pathToGarage = SentisOptimisationsPlugin.Config.PathToGarage;
                    var displayName = myCubeGrid.DisplayName;
                    // List<MyCubeGrid> grids = MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Mechanical)
                    //     .GetGroupNodes(myCubeGrid);
                    // grids.SortNoAlloc<MyCubeGrid>((Comparison<MyCubeGrid>) ((x, y) =>
                    //     x.BlocksCount.CompareTo(y.BlocksCount)));
                    // grids.Reverse();
                    // grids.SortNoAlloc<MyCubeGrid>((Comparison<MyCubeGrid>) ((x, y) =>
                    //     x.GridSizeEnum.CompareTo((object) y.GridSizeEnum)));
                    
                    List<MyCubeGrid> grids = new List<MyCubeGrid>();
                    grids.Add(myCubeGrid);
                    int index = 0;
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
                                        SentisOptimisationsPlugin.m_myProgrammableBlockKillProgramm.Invoke(
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
                        ++index;
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
                    newObject1.CubeGrids[0].DisplayName = hitEntity.DisplayName;
                    MyObjectBuilder_Definitions newObject2 =
                        MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Definitions>();
                    newObject2.ShipBlueprints = new MyObjectBuilder_ShipBlueprintDefinition[1];
                    newObject2.ShipBlueprints[0] = newObject1;

                    string str = Path.Combine(pathToGarage, MyAPIGateway.Players.TryGetSteamId(identityId).ToString());
                    if (!Directory.Exists(str))
                        Directory.CreateDirectory(str);
                    foreach (char ch in ((IEnumerable<char>) Path.GetInvalidPathChars()).Concat<char>((IEnumerable<char>) Path.GetInvalidFileNameChars()))
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
                    this.Context.Respond("Cтруктура " + hitEntity.DisplayName + " сохранена в гараж");

                    return;
                }
            }
        }

        [Command("load", ".", null)]
        [Permission(MyPromoteLevel.None)]
        public void Load(int index)
        {
            var path = Path.Combine(SentisOptimisationsPlugin.Config.PathToGarage, Context.Player.SteamUserId.ToString());
            var files = Directory.GetFiles(
                Path.Combine(SentisOptimisationsPlugin.Config.PathToGarage, Context.Player.SteamUserId.ToString()),
                "*.sbc");
            var listFiles = new List<string>(files).FindAll(s => s.EndsWith(".sbc"));
            listFiles.SortNoAlloc((s, s1) => String.Compare(s, s1, StringComparison.Ordinal));
            var gridNameToLoad = listFiles[index - 1];
            IMyPlayer player = this.Context.Player;
            if (player == null)
            {
                Context.Respond("Проиграл, попробуй ещё");
                return;
            }

            long identityId = player.IdentityId;
            IMyCharacter character = player.Character;
            
            float naturalGravityMultiplier;
            MyGravityProviderSystem.CalculateNaturalGravityInPoint(character.GetPosition(), out naturalGravityMultiplier);
            if (naturalGravityMultiplier > 0.5)
            {
                Context.Respond("Гараж недоступен в гравитации > 0.5");
                return;
            }
            var players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players);
            foreach (var myPlayer in players)
            {
                if (myPlayer.GetRelationTo(identityId) != MyRelationsBetweenPlayerAndBlock.Enemies)
                {
                    continue;
                }
                var distance = Vector3D.Distance(myPlayer.GetPosition(), character.GetPosition());
                if (distance < 10000)
                {
                    Context.Respond("Рядом враги, обернись.");
                    return;
                }
            }
            
            var spawnPosition = SpawnPosition(character, out var collisions);
            int tryCount = 0;
            while (tryCount < 5)
            {
                if (collisions.Count == 0)
                {
                    var gridPath = Path.Combine(path, gridNameToLoad);
                    DoSpawnGrids(identityId, gridPath, spawnPosition, (grid, identity) => AddGps(grid, identity));
                    Context.Respond("Структура перенесена в мир");
                    Log.Info("Структура" + gridNameToLoad + " перенесена в мир");
                    File.Move(gridPath, gridPath + "_spawned");
                    return;
                }
                spawnPosition = SpawnPosition(character, out var newcollisions);
                collisions = newcollisions;
                tryCount++;
            }
            
            Context.Respond("Слишком много всего вокруг, найдите место посвободнее");
            Log.Info("Слишком много всего вокруг, найдите место посвободнее " + gridNameToLoad);
        }

        private static Vector3D SpawnPosition(IMyCharacter character, out List<MyEntity> collisions)
        {
            var spawnPosition = new Vector3D(character.GetPosition().X + SentisOptimisationsPlugin._random.Next(-170, 170),
                character.GetPosition().Y + SentisOptimisationsPlugin._random.Next(-170, 170),
                character.GetPosition().Z + SentisOptimisationsPlugin._random.Next(-170, 170));


            BoundingSphereD boundingSphere = new BoundingSphereD(spawnPosition, 100);

            List<MyEntity> topMostEntitiesInSphere = MyEntities.GetEntitiesInSphere(ref boundingSphere);
            collisions = topMostEntitiesInSphere.FindAll(entity =>
                Vector3D.Distance(entity.PositionComp.GetPosition(), spawnPosition) < 100);
            return spawnPosition;
        }

        public static void AddGps(MyCubeGrid grid, long myPlayerIdentity)
        {
            grid.Physics?.SetSpeeds(Vector3.Zero, Vector3.Zero);
            grid.ConvertToStatic();
            var gridGPS =
                MyAPIGateway.Session?.GPS.Create(grid.DisplayName, grid.DisplayName, grid.PositionComp.GetPosition(), true, true);
            gridGPS.GPSColor = Color.Yellow;
            MyAPIGateway.Session?.GPS.AddGps(myPlayerIdentity, gridGPS);
        }
        public static void DoSpawnGrids(long masterIdentityId, string str, Vector3D spawnPosition,
           Delegate.AddListenerDelegate addListenerDelegate = null)
        {
            MyObjectBuilder_Definitions loadedPrefab = MyBlueprintUtils.LoadPrefab(str);
            MyObjectBuilder_CubeGrid[] cubeGrids = loadedPrefab.ShipBlueprints[0].CubeGrids;
            

            SpawnSomeGrids(cubeGrids, spawnPosition, masterIdentityId, addListenerDelegate);
        }

        public static void RemapOwnership(
            MyObjectBuilder_CubeGrid[] cubeGrids,
            long new_owner)
        {
            foreach (MyObjectBuilder_CubeGrid cubeGrid in cubeGrids)
            {
                foreach (MyObjectBuilder_CubeBlock cubeBlock in cubeGrid.CubeBlocks)
                {
                    cubeBlock.BuiltBy = new_owner;
                    cubeBlock.Owner = new_owner;
                    cubeBlock.ShareMode = MyOwnershipShareModeEnum.Faction;
                }
            }
        }

        public static void SpawnSomeGrids(MyObjectBuilder_CubeGrid[] cubeGrids,
            Vector3D position, long masterIdentityId, Delegate.AddListenerDelegate addListenerDelegate = null)
        {
            MyAPIGateway.Entities.RemapObjectBuilderCollection(cubeGrids);
            RemapOwnership(cubeGrids, masterIdentityId);
            Vector3D vector3D = cubeGrids[0].PositionAndOrientation.GetValueOrDefault().Position +
                                Vector3D.Zero;

            for (int index = 0; index < cubeGrids.Length; ++index)
            {
                MyObjectBuilder_CubeGrid cubeGrid = cubeGrids[index];

                if (index == 0)
                {
                    if (cubeGrid.PositionAndOrientation.HasValue)
                    {
                        MyPositionAndOrientation valueOrDefault = cubeGrid.PositionAndOrientation.GetValueOrDefault();
                        valueOrDefault.Position = position;
                        cubeGrid.PositionAndOrientation = new MyPositionAndOrientation?(valueOrDefault);
                        vector3D = cubeGrid.PositionAndOrientation.GetValueOrDefault().Position +
                                   Vector3D.Zero;
                    }
                }
                else
                {
                    MyPositionAndOrientation valueOrDefault = cubeGrid.PositionAndOrientation.GetValueOrDefault();
                    valueOrDefault.Position = valueOrDefault.Position - vector3D;
                    cubeGrid.PositionAndOrientation = valueOrDefault;
                }

            }

            //TODO: Добавить проверку на коллизии
            for (int index = 0; index < cubeGrids.Length; ++index)
            {
                MyAPIGateway.Entities.CreateFromObjectBuilderParallel(cubeGrids[index],
                    completionCallback: new Action<IMyEntity>(
                        entity =>
                        {
                            ((MyCubeGrid) entity).DetectDisconnectsAfterFrame();
                            MyAPIGateway.Entities.AddEntity(entity);
                            addListenerDelegate.Invoke(((MyCubeGrid) entity), masterIdentityId);
                        }));
            }
           
        }

        [Command("list", ".", null)]
        [Permission(MyPromoteLevel.None)]
        public void List()
        {
            try
            {
                IMyPlayer player = this.Context.Player;
                if (player == null)
                {
                    Context.Respond("Проиграл, попробуй ещё");
                    return;
                }

                long identityId = player.IdentityId;
                var pathToGarage = SentisOptimisationsPlugin.Config.PathToGarage;
                string str = Path.Combine(pathToGarage, MyAPIGateway.Players.TryGetSteamId(identityId).ToString());
                if (!Directory.Exists(str))
                {
                    Context.Respond("Гараж пуст");
                    return;
                }

                var files = Directory.GetFiles(
                    Path.Combine(SentisOptimisationsPlugin.Config.PathToGarage, Context.Player.SteamUserId.ToString()),
                    "*.sbc");
                var listFiles = new List<string>(files).FindAll(s => s.EndsWith(".sbc"));
                listFiles.SortNoAlloc((s, s1) => String.Compare(s, s1, StringComparison.Ordinal));
                var resultListFiles = new List<string>();
                listFiles.ForEach(s => resultListFiles.Add(s.Replace(".sbc", "")));
                string respond = "Структуры в гараже \n";
                for (var i = 1; i < resultListFiles.Count + 1; i++)
                {
                    respond = respond + i + ". " + Path.GetFileName(resultListFiles[i - 1]) + "\n";
                }

                Context.Respond(respond);
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }
    }
}