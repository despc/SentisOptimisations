using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.GUI;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations;
using VRage.Game;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin.ShipyardLogic
{
    public class Shipyard
    {
        
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static void OnClientBuy(ClientBuyRequest buyRequest)
        {
            var gridShipyardIdWithSelection = buyRequest.ShipyardId;
            MyProjectorBase projectorBase = (MyProjectorBase) MyEntities.GetEntityById(gridShipyardIdWithSelection);
            var steamSellerId = MySession.Static.Players.TryGetSteamId(projectorBase.BuiltBy);
            var playerGaragePath = Path.Combine(SentisOptimisationsPlugin.Config.PathToGarage, steamSellerId.ToString());
            var gridName = GetFromConfig(gridShipyardIdWithSelection);
            if (gridName == null)
            {
                SentisOptimisationsPlugin.Log.Error("gridShipyardIdWithSelection " + gridShipyardIdWithSelection + " gridName null");
                return;
            }

            var buyRequestSteamId = buyRequest.SteamId;
            string gridPath = Path.Combine(playerGaragePath, gridName);
            var clientGaragePath = Path.Combine(SentisOptimisationsPlugin.Config.PathToGarage, buyRequestSteamId.ToString());
            if (!Directory.Exists(clientGaragePath))
            {
                Directory.CreateDirectory(clientGaragePath);
            }
            string gridNewPath = Path.Combine(clientGaragePath, "NEW_" + Path.GetFileName(gridName));
            File.Move(gridPath, gridNewPath);
            var identityId = Sync.Players.TryGetIdentityId(buyRequestSteamId, 0);
            Log.Info("Продаём структуру " + Path.GetFileName(gridName) + " за " + buyRequest.Price);
            MyBankingSystem.ChangeBalance(identityId, -buyRequest.Price);
            var sellerIdentityId = projectorBase.BuiltBy;
            Log.Info("Продавец " + sellerIdentityId + " ( " + steamSellerId + ")");
            MyBankingSystem.ChangeBalance(sellerIdentityId, buyRequest.Price);
            projectorBase.Enabled = false;
            RemoveFromConfig(gridShipyardIdWithSelection);
        }

        public static void OnStartSell(StartSellRequest grid)
        {
            var gridShipyardIdWithSelection = grid.ShipyardId;
            MyProjectorBase projectorBase = (MyProjectorBase) MyEntities.GetEntityById(gridShipyardIdWithSelection);
            var steamUserId = PlayerUtils.GetPlayer(projectorBase.OwnerId).SteamUserId;
            var gridGridName = grid.ShipName;
            var playerGaragePath = Path.Combine(SentisOptimisationsPlugin.Config.PathToGarage, steamUserId.ToString());
            string gridPath = Path.Combine(playerGaragePath, gridGridName);
            AddToConfig(gridPath, gridShipyardIdWithSelection);


            Project(gridPath, projectorBase);
            SendGridListToClient(steamUserId, gridShipyardIdWithSelection);
        }

        private static void AddToConfig(string gridPath, long gridShipyardIdWithSelection)
        {
            var configConfigShipsInMarket = SentisOptimisationsPlugin.Config.ConfigShipsInMarket;
            foreach (var configShipInMarket in configConfigShipsInMarket)
            {
                if (configShipInMarket.ShipName.Equals(gridPath))
                {
                    configShipInMarket.ShipyardId = gridShipyardIdWithSelection;
                    return;
                }

                if (configShipInMarket.ShipyardId == gridShipyardIdWithSelection)
                {
                    configShipInMarket.ShipName = gridPath;
                    return;
                }
            }
            ConfigShipInMarket shipInMarket = new ConfigShipInMarket();
            shipInMarket.ShipName = gridPath;
            shipInMarket.ShipyardId = gridShipyardIdWithSelection;
            configConfigShipsInMarket.Add(shipInMarket);
            ConfigUtils.Save( SentisOptimisationsPlugin.Instance, SentisOptimisationsPlugin.Config, "SentisOptimisations.cfg");
        }
        
        private static void RemoveFromConfig(long gridShipyardIdWithSelection)
        {
            var configConfigShipsInMarket = SentisOptimisationsPlugin.Config.ConfigShipsInMarket;
            int indexToRemove = -1;
            foreach (var configShipInMarket in configConfigShipsInMarket)
            {
               
                if (configShipInMarket.ShipyardId == gridShipyardIdWithSelection)
                {
                    indexToRemove = configConfigShipsInMarket.IndexOf(configShipInMarket);
                    
                }
            }

            if (indexToRemove == -1)
            {
                return;
            }
            configConfigShipsInMarket.RemoveAt(indexToRemove);
            ConfigUtils.Save( SentisOptimisationsPlugin.Instance, SentisOptimisationsPlugin.Config, "SentisOptimisations.cfg");
        }
        
        private static string GetFromConfig(long gridShipyardIdWithSelection)
        {
            var configConfigShipsInMarket = SentisOptimisationsPlugin.Config.ConfigShipsInMarket;
            foreach (var configShipInMarket in configConfigShipsInMarket)
            {
                if (configShipInMarket.ShipyardId == gridShipyardIdWithSelection)
                {
                    return configShipInMarket.ShipName;
                }
            }

            return null;
        }

        private static void Project(string newName, MyProjectorBase projectorBase)
        {
            MyObjectBuilder_Definitions builderDefinitions = MyBlueprintUtils.LoadPrefab(newName);
            List<MyObjectBuilder_CubeGrid> list =
                ((IEnumerable<MyObjectBuilder_CubeGrid>) builderDefinitions.ShipBlueprints[0].CubeGrids)
                .ToList<MyObjectBuilder_CubeGrid>();
            // ModStorageSettings blockSettingsStorage = MainLogic.GetBlockSettingsStorage(info.Projector);
            ((IMyProjector) projectorBase).SetProjectedGrid(list[0]);
            Thread.Sleep(20);
            MethodInfo SendNewBlueprint =
                typeof(MyProjectorBase).GetMethod("SendNewBlueprint", BindingFlags.Instance | BindingFlags.NonPublic);
            SendNewBlueprint.Invoke((object) projectorBase, new object[1]
            {
                (object) list
            });
        }

        public static string SubstringBetween(string source, string start, string end)
        {
            int pFrom = source.IndexOf(start) + start.Length;
            int pTo = source.LastIndexOf(end);

          return source.Substring(pFrom, pTo - pFrom);
        }
        
        public static string SubstringBefore(string source, string end)
        {
            int pTo = source.LastIndexOf(end);

            return source.Substring(0, pTo);
        }

        public static void OnListRequest(byte[] data)
        {
            FillListRequest listRequest = MyAPIGateway.Utilities.SerializeFromBinary<FillListRequest>(data);
            var listRequestShipyardId = listRequest.ShipyardId;
            var listRequestSteamId = listRequest.SteamId;
            SendGridListToClient(listRequestSteamId, listRequestShipyardId);
        }

        private static void SendGridListToClient(ulong listRequestSteamId, long listRequestShipyardId)
        {
            if (listRequestSteamId == 0)
            {
                return;
            }
            var playerGaragePath = Path.Combine(SentisOptimisationsPlugin.Config.PathToGarage,
                listRequestSteamId.ToString());
            var files = Directory.GetFiles(playerGaragePath, "*.sbc");
            var listFiles = new List<string>(files).FindAll(s => s.EndsWith(".sbc"));
            listFiles.SortNoAlloc((s, s1) => String.Compare(s, s1, StringComparison.Ordinal));
            //var resultListFiles = new List<string>();
            //listFiles.ForEach(s => resultListFiles.Add(s.Replace(".sbc", "")));

            List<GridForList> gridsForList = new List<GridForList>();
            for (var i = 1; i < listFiles.Count + 1; i++)
            {
                GridForList gridForList = new GridForList();
                var fileName = Path.GetFileName(listFiles[i - 1]);
                string gridPath = Path.Combine(playerGaragePath, fileName);
                gridForList.GridName = fileName;
                MyEntity anotherShipyard;
                if (IsInTrade(gridPath, listRequestShipyardId, out anotherShipyard))
                {
                    gridForList.ShipyardIdWithSelection = listRequestShipyardId;
                    MyProjectorBase projectorBase = (MyProjectorBase) MyEntities.GetEntityById(listRequestShipyardId);
                    Project(listFiles[i - 1], projectorBase);
                    gridsForList.Add(gridForList);
                    continue;
                }

                if (anotherShipyard != null)
                {
                    continue;
                }
                gridForList.ShipyardIdWithSelection = 0;
                gridsForList.Add(gridForList);
            }

            FillListResponse response = new FillListResponse();
            response.Grids = gridsForList;

            response.ShipyardId = listRequestShipyardId;
            Communication.SendToClient(MessageType.SetGridListResp,
                MyAPIGateway.Utilities.SerializeToBinary(response), listRequestSteamId);
        }

        public static bool IsInTrade(string fileName, long reqShipyardId, out MyEntity entityById)
        {
            entityById = null;
            var configConfigShipsInMarket = SentisOptimisationsPlugin.Config.ConfigShipsInMarket;
            foreach (var configShipInMarket in configConfigShipsInMarket)
            {
                if (configShipInMarket.ShipName.Equals(fileName))
                {
                    var shipyardId = configShipInMarket.ShipyardId;
                    if (reqShipyardId == shipyardId)
                    {
                        return true;
                    }
                    entityById = MyEntities.GetEntityById(shipyardId);
                    return false;
                }
            }
            return false;
        }
    }
}