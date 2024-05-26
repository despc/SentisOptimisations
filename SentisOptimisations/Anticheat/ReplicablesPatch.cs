using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NAPI;
using NLog;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Replication;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using Torch.Utils;
using VRage.Collections;
using VRage.Network;
using VRage.Replication;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class ReplicablesPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        [ReflectedGetter(Name = "m_clientStates")]
        private static Func<MyReplicationServer, IDictionary> _clientStates;
        
        [ReflectedGetter(TypeName = "VRage.Network.MyClient, VRage", Name = "Replicables")]
        private static Func<object, MyConcurrentDictionary<IMyReplicable, MyReplicableClientData>> _replicables;
        
        [ReflectedMethod(Name = "RemoveForClient", OverrideTypeNames = new string[] { null, "VRage.Network.MyClient, VRage", null })]
        private static Action<MyReplicationServer, IMyReplicable, object, bool> _removeForClient;
        
        public static void Patch(PatchContext ctx)
        {
            
            var MethodGetReplicablesInBox = typeof(MyReplicablesAABB).GetMethod
                (nameof(MyReplicablesAABB.GetReplicablesInBox), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

            ctx.GetPattern(MethodGetReplicablesInBox).Suffixes.Add(
                typeof(ReplicablesPatch).GetMethod(nameof(GetReplicablesInBoxPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodSendUpdate = typeof(MyReplicationServer).GetMethod
                (nameof(MyReplicationServer.SendUpdate), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            
            ctx.GetPattern(MethodSendUpdate).Suffixes.Add(
                typeof(ReplicablesPatch).GetMethod(nameof(SendUpdatePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static void SendUpdatePatched(MyReplicationServer __instance)
        {
            try
            {
                var clientDataDict = _clientStates.Invoke(__instance);
                foreach (Object clientState in clientDataDict.Values)
                {
                    object clientData = clientState;

                    MyClientStateBase State = (MyClientStateBase)clientData.easyGetField("State");
                    var isAdmin = PlayerUtils.IsAdmin(PlayerUtils.GetPlayer(State.EndpointId.Id.Value));
                    if (isAdmin)
                    {
                        continue;
                    }

                    Vector3D? position = State.Position;

                    if (!position.HasValue)
                    {
                        continue;
                    }

                    var clientReplicables = _replicables.Invoke(clientData);
                    var replicableList = new List<IMyReplicable>(clientReplicables.Count);

                    replicableList.AddRange(from pair in clientReplicables
                        select pair.Key);
                    foreach (var myReplicable in replicableList)
                    {
                        var charReplicable = myReplicable as MyEntityReplicableBaseEvent<MyCharacter>;
                        var charPos = charReplicable?.Instance?.PositionComp?.GetPosition();
                        if (charPos != null)
                        {
                            if (State.EndpointId.Id.Value == charReplicable.Instance.ControlSteamId)
                            {
                                continue;
                            }

                            if (Vector3D.Distance(position.Value, charPos.Value) >
                                SentisOptimisationsPlugin.Config.PlayersSyncDistance)
                            {
                                _removeForClient.Invoke(__instance, myReplicable, clientData, true);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "SendUpdatePatched Exception");
            }
        }
        
        private static void GetReplicablesInBoxPatched(MyReplicablesAABB __instance, BoundingBoxD aabb, List<IMyReplicable> list)
        {
            List<IMyReplicable> replicablesToFilter = new List<IMyReplicable>();
            foreach (var myReplicable in list)
            {
                var charReplicable = myReplicable as MyEntityReplicableBaseEvent<MyCharacter>;
                var charPos = charReplicable?.Instance?.PositionComp?.GetPosition();
                if (charPos != null)
                {
                    if (Vector3D.Distance(aabb.Center, charPos.Value) > SentisOptimisationsPlugin.Config.PlayersSyncDistance)
                    {
                        replicablesToFilter.Add(myReplicable);
                    }
                }
            }
            foreach (var replicableToFilter in replicablesToFilter)
            {
                list.Remove(replicableToFilter);
            }
        }
    }
}