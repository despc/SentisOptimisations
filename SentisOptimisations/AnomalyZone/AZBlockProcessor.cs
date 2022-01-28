using System;
using System.Collections.Generic;
using NLog;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisOptimisationsPlugin.AnomalyZone
{
    public class AZBlockProcessor
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private Dictionary<long, int> ActiveEnemiesPerFaction = new Dictionary<long, int>();

        bool ActivateOnCharacter = false;
        bool ActivateOnLargeGrid = true;
        bool ActivateOnSmallGrid = true;
        bool IgnoreCopilot = true;
        float ContestedDrainRate = 0;
        float Progress = 0;
        public float IdleDrainRate = 3;
        private int lastPlayerCount = 0;
        public long ControlledBy;

        public enum ZoneStates
        {
            Active,
            Idle,
            Contested
        }

        private ZoneStates lastState = ZoneStates.Idle;
        public ZoneStates State { get; private set; } = ZoneStates.Idle;

        private IMyFaction controlledByFaction = null;


        public MySafeZoneBlock mySafeZoneBlock;
        public MySafeZone mySafeZone;

        public AZBlockProcessor(MySafeZoneBlock mySafeZoneBlock, MySafeZone mySafeZone)
        {
            this.mySafeZoneBlock = mySafeZoneBlock;
            this.mySafeZone = mySafeZone;
            OnPlayerDied += PlayerDied;
            OnUpdate += ZoneUpdate;
            OnAwardPoints += AZReward.AwardPointsAndRewards;
        }

        private void ZoneUpdate(AZBlockProcessor processor)
        {
            SaveData();
            //Network.SendCommand("sync_zone", data: MyAPIGateway.Utilities.SerializeToBinary(zone.Data));
        }

        public void SaveData()
        {
            // if (!MyAPIGateway.Multiplayer.IsServer) return;
            //
            // Session session = new Session();
            // foreach (ScoreDescription score in Scores.Values)
            // {
            //     session.Scores.Add(score);
            // }
            // Descriptions.Session.Save(session);
            //
            // foreach (ZoneBlock b in Zones)
            // {
            //     b.Data.Save(b.Entity);
            //     strEntityId = b.Entity.ToString();
            // }
        }


        public IMyFaction ControlledByFaction
        {
            get { return controlledByFaction; }
            set
            {
                controlledByFaction = value;
                if (value == null)
                {
                    return;
                }

                ControlledBy = value.FactionId;
            }
        }

        public void Update()
        {
            try
            {
                var radius = 3000;
                // if (!IsInitialized)
                // {
                //     CreateControls();
                //     IsInitialized = true;
                // }

                if (!mySafeZoneBlock.IsFunctional || !mySafeZoneBlock.Enabled || !mySafeZoneBlock.IsWorking)
                    return; // if the block is incomplete or turned off
                MatrixD matrix = mySafeZoneBlock.WorldMatrix;
                Vector3D location = matrix.Translation;

                //IMyPlayer localPlayer = MyAPIGateway.Session.LocalHumanPlayer;

                List<IMyPlayer> players = new List<IMyPlayer>();
                List<IMyPlayer> playersInZone = new List<IMyPlayer>();
                List<IMyFaction> factionsInZone = new List<IMyFaction>();

                List<IMyPlayer> validatedPlayers = new List<IMyPlayer>(); // players that meet activation criteria
                Dictionary<long, int> validatedPlayerCountByFaction = new Dictionary<long, int>();
                List<IMyCubeGrid> validatedGrids = new List<IMyCubeGrid>();
                IMyFaction nominatedFaction = null;

                MyAPIGateway.Players.GetPlayers(players);

                foreach (IMyPlayer p in players)
                {
                    if (p.Character != null)
                    {
                        p.Character.CharacterDied -= Died;
                    }

                    if (Vector3D.Distance(p.GetPosition(), location) > radius) continue;
                    playersInZone.Add(p);

                    if (p.Character != null)
                    {
                        p.Character.CharacterDied += Died;
                    }


                    // bool activateOnCharacter = false;
                    // bool activateOnCharacter = false;
                    if (!ActivateOnCharacter && !(p.Controller.ControlledEntity is IMyCubeBlock)) continue;

                    IMyFaction f = MyAPIGateway.Session.Factions.TryGetPlayerFaction(p.IdentityId);
                    if (f == null) continue;

                    validatedPlayers.Add(p);
                    List<IMySlimBlock> temp = new List<IMySlimBlock>();
                    if ((p.Controller.ControlledEntity is IMyCubeBlock))
                    {
                        IMyCubeBlock cube = (p.Controller.ControlledEntity as IMyCubeBlock);
                        IMyCubeGrid grid = cube.CubeGrid;

                        if (grid.IsStatic) continue;

                        if (!cube.IsWorking) continue;

                        if (!ActivateOnCharacter)
                        {
                            if (grid.GridSizeEnum == MyCubeSize.Large)
                            {
                                if (!ActivateOnLargeGrid)
                                {
                                    validatedPlayers.Remove(p);
                                    continue;
                                }

                                int blockCount = 0;
                                grid.GetBlocks(temp, (block) =>
                                {
                                    blockCount++;
                                    return false;
                                });
                                if (blockCount < SentisOptimisationsPlugin.Config.AzMinLargeGridBlockCount)
                                {
                                    validatedPlayers.Remove(p);
                                    continue;
                                }
                            }
                            else if (grid.GridSizeEnum == MyCubeSize.Small)
                            {
                                if (!ActivateOnSmallGrid)
                                {
                                    validatedPlayers.Remove(p);
                                    continue;
                                }

                                int blockCount = 0;
                                grid.GetBlocks(temp, (block) =>
                                {
                                    blockCount++;
                                    return false;
                                });
                                if (blockCount < SentisOptimisationsPlugin.Config.AzMinSmallGridBlockCount)
                                {
                                    validatedPlayers.Remove(p);
                                    continue;
                                }
                            }
                        }

                        if (IgnoreCopilot)
                        {
                            if (validatedGrids.Contains(grid))
                            {
                                validatedPlayers.Remove(p);
                                continue;
                            }
                            else
                            {
                                validatedGrids.Add(grid);
                            }
                        }
                    }

                    if (nominatedFaction == null)
                    {
                        nominatedFaction = f;
                    }

                    if (!ActiveEnemiesPerFaction.ContainsKey(f.FactionId))
                    {
                        ActiveEnemiesPerFaction.Add(f.FactionId, 0);
                    }

                    if (!validatedPlayerCountByFaction.ContainsKey(f.FactionId))
                    {
                        validatedPlayerCountByFaction.Add(f.FactionId, 1);
                        factionsInZone.Add(f);
                    }
                    else
                    {
                        validatedPlayerCountByFaction[f.FactionId]++;
                    }
                }

                bool isContested = false;
                for (int i = 0; i < factionsInZone.Count; i++)
                {
                    for (int j = 0; j < factionsInZone.Count; j++)
                    {
                        if (factionsInZone[i] == factionsInZone[j]) continue;

                        if (MyAPIGateway.Session.Factions.GetRelationBetweenFactions(factionsInZone[i].FactionId,
                            factionsInZone[j].FactionId) == MyRelationsBetweenFactions.Enemies)
                        {
                            isContested = true;
                            break;
                        }
                    }
                }

                int factionCount = validatedPlayerCountByFaction.Keys.Count;
                Color color = Color.Gray;
                lastState = State;

                float speed = 0;
                if (isContested)
                {
                    State = ZoneStates.Contested;
                    color = Color.Red;
                    speed = -GetProgress(ContestedDrainRate);
                    Progress += speed;

                    if (ControlledByFaction == null)
                    {
                        ControlledByFaction = nominatedFaction;
                    }
                }
                else if (factionCount == 0)
                {
                    State = ZoneStates.Idle;
                    color = Color.Black;
                    ControlledByFaction = null;
                    speed = -GetProgress(IdleDrainRate);
                    Progress += speed;
                }
                else
                {
                    State = ZoneStates.Active;
                    color = Color.White;
                    speed = GetProgress(validatedPlayers.Count);
                    Progress += speed;
                    ControlledByFaction = nominatedFaction;

                    foreach (IMyFaction zoneFaction in factionsInZone)
                    {
                        int enemyCount = 0;
                        foreach (IMyPlayer p in players)
                        {
                            IMyFaction f = MyAPIGateway.Session.Factions.TryGetPlayerFaction(p.IdentityId);

                            if (f == null || f.FactionId == zoneFaction.FactionId) continue;

                            if (MyAPIGateway.Session.Factions.GetRelationBetweenFactions(f.FactionId,
                                zoneFaction.FactionId) == MyRelationsBetweenFactions.Enemies)
                            {
                                enemyCount++;
                            }
                        }

                        if (ActiveEnemiesPerFaction[zoneFaction.FactionId] < enemyCount)
                        {
                            ActiveEnemiesPerFaction[zoneFaction.FactionId] = enemyCount;
                        }
                    }
                }

                if (Progress >= SentisOptimisationsPlugin.Config.AzProgressWhenComplete)
                {
                    OnAwardPoints.Invoke(mySafeZoneBlock, ControlledByFaction);
                    ResetActiveEnemies();
                    Progress = 0;
                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    {
                        TriggerTerminalRefresh(mySafeZoneBlock.CubeGrid.EntityId, ControlledByFaction.FactionId);
                    });
                }

                if (Progress <= 0)
                {
                    Progress = 0;
                }

                foreach (var localPlayer in playersInZone)
                {
                    if (localPlayer != null && playersInZone.Contains(localPlayer))
                    {
                        int allies = 0;
                        int enemies = 0;
                        int neutral = 0;
                        foreach (IMyPlayer p in playersInZone)
                        {
                            if (!ActivateOnCharacter && !(p.Controller.ControlledEntity is IMyCubeBlock)) continue;

                            switch (localPlayer.GetRelationTo(p.IdentityId))
                            {
                                case MyRelationsBetweenPlayerAndBlock.Owner:
                                case MyRelationsBetweenPlayerAndBlock.FactionShare:
                                    allies++;
                                    break;
                                case MyRelationsBetweenPlayerAndBlock.Neutral:
                                    neutral++;
                                    break;
                                case MyRelationsBetweenPlayerAndBlock.Enemies:
                                case MyRelationsBetweenPlayerAndBlock.NoOwnership:
                                    enemies++;
                                    break;
                            }
                        }

                        string specialColor = "White";
                        switch (State)
                        {
                            case ZoneStates.Contested:
                                specialColor = "Red";
                                break;
                            case ZoneStates.Active:
                                specialColor = "Blue";
                                break;
                        }

                        MyVisualScriptLogicProvider.ShowNotification(
                            $"Allies: {allies}  Neutral: {neutral}  Enemies: {enemies} - {State.ToString().ToUpper()}: {((Progress / SentisOptimisationsPlugin.Config.AzProgressWhenComplete) * 100).ToString("n0")}% Speed: {speed * 100}% {(ControlledByFaction != null ? $"Controlled by: {ControlledByFaction.Tag}" : "")}",
                            SentisOptimisationsPlugin.Config.AzMessageTime, specialColor, localPlayer.IdentityId);
                    }
                }

                MySafeZoneComponent component = mySafeZoneBlock.Components.Get<MySafeZoneComponent>();
                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    try
                    {
                        var drawSphereRequest = new DrawSphereRequest();
                        drawSphereRequest.blockId = mySafeZoneBlock.EntityId;
                        switch (State)
                        {
                            case ZoneStates.Contested:
                                drawSphereRequest.color = "Red";
                                break;
                            case ZoneStates.Active:
                                drawSphereRequest.color = "Blue";
                                break;
                        }
                        Communication.BroadcastToClients(MessageType.DrawSphere, MyAPIGateway.Utilities.SerializeToBinary(drawSphereRequest));
                        // ReflectionUtils.InvokeInstanceMethod(typeof(MySafeZoneComponent), component, "SetColor",
                        //     new object[] {color});
                        
                    }
                    catch (Exception e)
                    {
                        //
                    }

                });

                if (MyAPIGateway.Multiplayer.IsServer && playersInZone.Count != lastPlayerCount)
                {
                    lastPlayerCount = playersInZone.Count;
                    OnUpdate.Invoke(this);
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private void PlayerDied(IMyPlayer player, IMyFaction faction)
        {
            if (SentisOptimisationsPlugin.Config.AzPointsRemovedOnDeath <= 0) return;
            MyAPIGateway.Utilities.InvokeOnGameThread(() =>
            {
                string message =
                    $"[{faction.Tag}] {player.DisplayName} Died: -{SentisOptimisationsPlugin.Config.AzPointsRemovedOnDeath} point";
                Log.Warn(message);
                ChatUtils.SendToAll(message);
                MyVisualScriptLogicProvider.ShowNotification(message, 5000, "Red");
            });
            AZReward.ChangePoints(mySafeZoneBlock, faction, -SentisOptimisationsPlugin.Config.AzPointsRemovedOnDeath);
        }

        public static event Action<IMyPlayer, IMyFaction> OnPlayerDied = delegate { };

        private void Died(IMyCharacter character)
        {
            List<IMyPlayer> players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players);

            foreach (IMyPlayer p in players)
            {
                if (p.Character == character)
                {
                    IMyFaction f = MyAPIGateway.Session.Factions.TryGetPlayerFaction(p.IdentityId);
                    if (f != null)
                    {
                        OnPlayerDied.Invoke(p, f);
                    }

                    break;
                }
            }
        }

        private float GetProgress(float progressModifier)
        {
            return (((float) progressModifier * (float) progressModifier - 1) /
                    ((float) progressModifier * (float) progressModifier + (3 * (float) progressModifier) + 1)) + 1;
        }

        private void ResetActiveEnemies()
        {
            Dictionary<long, int> newDict = new Dictionary<long, int>();

            foreach (long key in ActiveEnemiesPerFaction.Keys)
            {
                newDict.Add(key, 0);
            }

            ActiveEnemiesPerFaction = newDict;
        }

        public static void TriggerTerminalRefresh(long gridEntityId, long TAG)
        {
            IMyCubeGrid grid = MyAPIGateway.Entities.GetEntityById(gridEntityId) as IMyCubeGrid;
            var gts = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid);
            var blockList = new List<MyCubeBlock>();
            gts.GetBlocksOfType(blockList);

            foreach (var cube in blockList)
            {
                if (cube != null && cube is IMyCargoContainer && ((IMyCargoContainer)cube).CustomData.Contains("Prizebox"))
                {
                    var founderId = MySession.Static.Factions.TryGetFactionById(TAG).FounderId;
                    MyVisualScriptLogicProvider.ChangeOwner(cube.Name, founderId, true);
                    //cube.CubeGrid.ChangeOwnerRequest(cube.CubeGrid, cube, founderId, MyOwnershipShareModeEnum.Faction);
                }
            }
        }

        public static event Action<MySafeZoneBlock, IMyFaction> OnAwardPoints = delegate { };

        public static event Action<AZBlockProcessor> OnUpdate = delegate { };
    }
}