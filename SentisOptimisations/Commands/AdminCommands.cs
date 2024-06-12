using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using Sandbox.Engine.Physics;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisGameplayImprovements.AllGridsActions;
using SentisOptimisations.DelayedLogic;
using Torch.Commands;
using Torch.Commands.Permissions;
using Torch.Managers;
using Torch.Utils;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;
using IMyProjector = Sandbox.ModAPI.Ingame.IMyProjector;

namespace SentisOptimisations.Commands
{
    // !ainpc clean скопировано у Dori - источник - https://github.com/dorimanx/Essentials/blob/master/Essentials/Commands/WorldModule.cs

    public class AdminCommands : CommandModule
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

#pragma warning disable CS0649 // is never assigned to, and will always have its default value null
#pragma warning disable IDE0044 // Add readonly modifier
        [ReflectedGetter(Name = "m_relationsBetweenFactions", Type = typeof(MyFactionCollection))]
        private static
            Func<MyFactionCollection,
                Dictionary<MyFactionCollection.MyRelatablePair, Tuple<MyRelationsBetweenFactions, int>>> _relationsGet;

        [ReflectedGetter(Name = "m_relationsBetweenPlayersAndFactions", Type = typeof(MyFactionCollection))]
        private static
            Func<MyFactionCollection,
                Dictionary<MyFactionCollection.MyRelatablePair, Tuple<MyRelationsBetweenFactions, int>>>
            _playerRelationsGet;
#pragma warning restore IDE0044 // Add readonly modifier
#pragma warning restore CS0649 // is never assigned to, and will always have its default value null

#pragma warning disable IDE0044 // Add readonly modifier
        private static MethodInfo _factionChangeSuccessInfo =
            typeof(MyFactionCollection).GetMethod("FactionStateChangeSuccess",
                BindingFlags.NonPublic | BindingFlags.Static);
#pragma warning restore IDE0044 // Add readonly modifier

        private static readonly FieldInfo GpsDicField =
            typeof(MyGpsCollection).GetField("m_playerGpss", BindingFlags.NonPublic | BindingFlags.Instance);


        [Command("set_hydro", "Set hydrogen level of all tanks on grid")]
        [Permission(MyPromoteLevel.Admin)]
        public void SetHydroLevel(float newValue = 0.5f)
        {
            IMyPlayer player = Context.Player;
            if (player == null)
                return;
            var character = player.Character;
            Matrix headMatrix = character.GetHeadMatrix(true, true, false);
            Vector3D vector3D = headMatrix.Translation + headMatrix.Forward * 0.5f;
            Vector3D worldEnd = headMatrix.Translation + headMatrix.Forward * 5000.5f;
            List<MyPhysics.HitInfo> mRaycastResult = new List<MyPhysics.HitInfo>();

            MyPhysics.CastRay(vector3D, worldEnd, mRaycastResult, 15);

            foreach (var hitInfo in new HashSet<MyPhysics.HitInfo>(mRaycastResult))
            {
                if (hitInfo.HkHitInfo.GetHitEntity() is MyCubeGrid grid)
                {
                    // ignore projected grid.
                    if (grid.IsPreview)
                        continue;

                    foreach (var myCubeBlock in grid.GetFatBlocks())
                    {
                        if (myCubeBlock is MyGasTank)
                        {
                            ((MyGasTank)myCubeBlock).ChangeFillRatioAmount(newValue);
                        }
                    }
                    Context.Respond($"Водород для грида {grid.DisplayName} выставлен на {newValue * 100}%");
                    return;
                }
            }
        }

        [Command("clean_projectors", "Remove projections from all projectors")]
        [Permission(MyPromoteLevel.Admin)]
        public void ClearProjectors()
        {
            DelayedProcessor.Instance.AddDelayedAction(DateTime.Now, () =>
            {
                foreach (var myCubeGrid in new HashSet<MyCubeGrid>(EntitiesObserver.MyCubeGrids))
                {
                    if (myCubeGrid == null || myCubeGrid.Closed || myCubeGrid.MarkedForClose)
                    {
                        continue;
                    }
                    if (myCubeGrid.IsPreview)
                    {
                        continue;
                    }

                    foreach (var myProjectorBase in myCubeGrid.GetFatBlocks<MyProjectorBase>())
                    {
                        if (myProjectorBase.ProjectedGrid != null)
                        {
                            MyAPIGateway.Utilities.InvokeOnGameThread((Action)(() =>
                            {
                                try
                                {
                                    SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Info($"Cleaned projector {myProjectorBase.CustomName} on grid {myCubeGrid.DisplayName}");
                                    myProjectorBase.Enabled = false;
                                    ((Sandbox.ModAPI.IMyProjector)myProjectorBase).SetProjectedGrid(null);
                                }
                                catch (Exception ex)
                                {
                                    SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(ex, "clean projectors error");
                                }
                            }));
                        }
                    }
                    
                }
                
            });
        }

        [Command("ai_npc clean", "Cleans up NPC junk data from the sandbox file")]
        [Permission(MyPromoteLevel.Admin)]
        public void AI_NPC_Clean()
        {
            int count = 0;
            var validIdentities = new HashSet<long>();
            var idCache = new HashSet<long>();

            //find all identities owning a block
            foreach (var entity in MyEntities.GetEntities())
            {
                if (!(entity is MyCubeGrid grid))
                    continue;

                validIdentities.UnionWith(grid.SmallOwners);
            }

            foreach (var online in MySession.Static.Players.GetOnlinePlayers())
            {
                validIdentities.Add(online.Identity.IdentityId);
            }

            //might not be necessary, but just in case
            validIdentities.Remove(0);

            List<string> npc_model = new List<string>
                { "Shadow_Bot", "Space_Zombie", "Drone_Bot", "Alien_OB", "Mutant" };

            foreach (var identity in MySession.Static.Players.GetAllIdentities().ToList())
            {
                if (npc_model.Contains(identity.Model)) // Доп проверки для НПС не реализуем т.к. чистим всех
                {
                    RemoveFromFaction_Internal(identity);

                    // Две строчки ниже по ощущениям тот еще костыль
                    MySession.Static.Players.TryGetPlayerId(identity.IdentityId, out MyPlayer.PlayerId player_id);

                    if (MySession.Static.Players.TryGetPlayerById(player_id, out MyPlayer player))
                    {
                        MySession.Static.Players.RemovePlayer(player, true);
                        count++;
                    }

                    MySession.Static.Players.RemoveIdentity(identity.IdentityId, default);
                    validIdentities.Remove(identity
                        .IdentityId); // Удаляем айдишник НПС из списка валидных чтобы почистило репу и остальной мусор
                    count++;
                }
            }

            //reset ownership of blocks belonging to deleted identities
            count += FixBlockOwnership();

            //clean up empty factions
            count += CleanFaction_Internal();

            //cleanup reputations
            count += CleanupReputations();

            //Keen, for the love of god why is everything about GPS internal.
            var playerGpss = GpsDicField.GetValue(MySession.Static.Gpss) as Dictionary<long, Dictionary<int, MyGps>>;
            foreach (var id in playerGpss.Keys)
            {
                if (!validIdentities.Contains(id))
                    idCache.Add(id);
            }

            foreach (var id in idCache)
                playerGpss.Remove(id);

            count += idCache.Count;
            idCache.Clear();

            Context.Respond($"Removed {count} unnecessary AI-NPC elements.");
        }

        private static int CleanupReputations()
        {
            var collection = _relationsGet(MySession.Static.Factions);
            var collection2 = _playerRelationsGet(MySession.Static.Factions);
            var validIdentities = new HashSet<long>();

            //find all identities owning a block
            foreach (var entity in MyEntities.GetEntities())
            {
                if (!(entity is MyCubeGrid grid))
                    continue;

                validIdentities.UnionWith(grid.SmallOwners);
            }

            //find online identities
            foreach (var online in MySession.Static.Players.GetOnlinePlayers())
            {
                validIdentities.Add(online.Identity.IdentityId);
            }

            foreach (var identity in MySession.Static.Players.GetAllIdentities().ToList())
            {
                if (MySession.Static.Players.IdentityIsNpc(identity.IdentityId))
                    validIdentities.Add(identity.IdentityId);
            }

            //Add Factions with at least one member to valid identities
            foreach (var faction in MySession.Static.Factions.Factions.Where(x => x.Value.Members.Count > 0))
            {
                validIdentities.Add(faction.Key);
            }

            //might not be necessary, but just in case
            validIdentities.Remove(0);
            var result = 0;
            var collection0List = collection.Keys.ToList();
            var collection1List = collection2.Keys.ToList();

            foreach (var pair in collection0List)
            {
                if (validIdentities.Contains(pair.RelateeId1) && validIdentities.Contains(pair.RelateeId2))
                    continue;
                collection.Remove(pair);
                result++;
            }

            foreach (var pair in collection1List)
            {
                if (validIdentities.Contains(pair.RelateeId1) && validIdentities.Contains(pair.RelateeId2))
                    continue;
                collection2.Remove(pair);
                result++;
            }

            //_relationsSet.Invoke(MySession.Static.Factions,collection);
            //_playerRelationsSet.Invoke(MySession.Static.Factions,collection2);
            return result;
        }

        private static int CleanFaction_Internal(int memberCount = 1)
        {
            int result = 0;

            foreach (var faction in MySession.Static.Factions.ToList())
            {
                if ((faction.Value.IsEveryoneNpc() || !faction.Value.AcceptHumans) &&
                    faction.Value.Members.Count != 0) //needed to add this to catch the 0 member factions
                    continue;

                int validmembers = 0;
                //O(2n)
                foreach (var member in faction.Value.Members)
                {
                    if (!MySession.Static.Players.HasIdentity(member.Key) &&
                        !MySession.Static.Players.IdentityIsNpc(member.Key))
                        continue;

                    validmembers++;
                    if (validmembers >= memberCount)
                        break;
                }

                if (validmembers >= memberCount)
                    continue;

                RemoveFaction(faction.Value);
                result++;
            }

            return result;
        }
        
        private static bool RemoveFromFaction_Internal(MyIdentity identity)
        {
            var fac = MySession.Static.Factions.GetPlayerFaction(identity.IdentityId);
            if (fac == null)
                return false;

            /*
             * VisualScriptLogicProvider takes care of removal of faction if last
             * identity is kicked, and promotes the next player in line to Founder
             * if the founder is being kicked.
             *
             * Factions must have a founder otherwise calls like MyFaction.Members.Keys will NRE.
             */
            MyVisualScriptLogicProvider.KickPlayerFromFaction(identity.IdentityId);

            return true;
        }


        //TODO: This should probably be moved into Torch base, but I honestly cannot be bothered
        /// <summary>
        /// Removes a faction from the server and all clients because Keen fucked up their own system.
        /// </summary>
        /// <param name="faction"></param>
        private static void RemoveFaction(MyFaction faction)
        {
            //bypass the check that says the server doesn't have permission to delete factions
            //_applyFactionState(MySession.Static.Factions, MyFactionStateChange.RemoveFaction, faction.FactionId, faction.FactionId, 0L, 0L);
            //MyMultiplayer.RaiseStaticEvent(s =>
            //        (Action<MyFactionStateChange, long, long, long, long>) Delegate.CreateDelegate(typeof(Action<MyFactionStateChange, long, long, long, long>), _factionStateChangeReq),
            //    MyFactionStateChange.RemoveFaction, faction.FactionId, faction.FactionId, faction.FounderId, faction.FounderId);
            NetworkManager.RaiseStaticEvent(_factionChangeSuccessInfo, MyFactionStateChange.RemoveFaction,
                faction.FactionId, faction.FactionId, 0L, 0L);

            if (!MyAPIGateway.Session.Factions.FactionTagExists(faction.Tag))
                return;

            MyAPIGateway.Session.Factions
                .RemoveFaction(faction.FactionId); //Added to remove factions that got through the crack
        }

        private static int FixBlockOwnership()
        {
            int count = 0;
            foreach (var entity in MyEntities.GetEntities())
            {
                if (!(entity is MyCubeGrid grid))
                    continue;

                var owner = grid.BigOwners.FirstOrDefault();
                var share = owner == 0 ? MyOwnershipShareModeEnum.All : MyOwnershipShareModeEnum.Faction;
                foreach (var block in grid.GetFatBlocks())
                {
                    if (block.OwnerId == 0 || MySession.Static.Players.HasIdentity(block.OwnerId))
                        continue;

                    block.ChangeOwner(owner, share);
                    count++;
                }
            }

            return count;
        }
    }
}