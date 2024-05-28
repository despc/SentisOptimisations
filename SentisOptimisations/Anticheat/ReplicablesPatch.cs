using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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

        private static FieldInfo StateField;
        
        [ReflectedGetter(TypeName = "VRage.Network.MyClient, VRage", Name = "Replicables")]
        private static Func<object, MyConcurrentDictionary<IMyReplicable, MyReplicableClientData>> _replicables;
        
        [ReflectedMethod(Name = "RemoveForClient", OverrideTypeNames = new string[] { null, "VRage.Network.MyClient, VRage", null })]
        private static Action<MyReplicationServer, IMyReplicable, object, bool> _removeForClient;
        
        public static void Patch(PatchContext ctx)
        {
            
            // var MethodGetReplicablesInBox = typeof(MyReplicablesAABB).GetMethod
            //     (nameof(MyReplicablesAABB.GetReplicablesInBox), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            //
            // ctx.GetPattern(MethodGetReplicablesInBox).Suffixes.Add(
            //     typeof(ReplicablesPatch).GetMethod(nameof(GetReplicablesInBoxPatched),
            //         BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            // var MethodSendUpdate = typeof(MyReplicationServer).GetMethod
            //     (nameof(MyReplicationServer.SendUpdate), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            //
            // ctx.GetPattern(MethodSendUpdate).Suffixes.Add(
            //     typeof(ReplicablesPatch).GetMethod(nameof(SendUpdatePatched),
            //         BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var assembly = typeof(MyReplicationServer).Assembly;
            var MyClientType = assembly.GetType("VRage.Network.MyClient");

            StateField = MyClientType.GetField("State",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            
            var MethodCalculateLayerOfReplicable = MyClientType.GetMethod
                ("CalculateLayerOfReplicable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            
            ctx.GetPattern(MethodCalculateLayerOfReplicable).Prefixes.Add(
                typeof(ReplicablesPatch).GetMethod(nameof(CalculateLayerOfReplicablePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodAddReplicableToLayer = typeof(MyReplicationServer).GetMethod
            ("AddReplicableToLayer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            
            ctx.GetPattern(MethodAddReplicableToLayer).Prefixes.Add(
                typeof(ReplicablesPatch).GetMethod(nameof(AddReplicableToLayerPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

         private static bool AddReplicableToLayerPatched(MyReplicationServer __instance, IMyReplicable rep,
             Object layer, Object client, bool __result)
        {
            try
            {
                if (SentisOptimisationsPlugin.Config.PlayersSyncDistance < 0)
                {
                    return true;
                }
                var charReplicable = rep as MyEntityReplicableBaseEvent<MyCharacter>;
                if (charReplicable != null)
                {
                    MyClientStateBase State = (MyClientStateBase) StateField.GetValue(client);
                    var isAdmin = PlayerUtils.IsAdmin(PlayerUtils.GetPlayer(State.EndpointId.Id.Value));
                    if (isAdmin)
                    {
                        return true;
                    }
                
                    var charPos = charReplicable.Instance?.PositionComp?.GetPosition();
                    if (charPos != null)
                    {
                        if (State.EndpointId.Id.Value == charReplicable.Instance.ControlSteamId)
                        {
                            return true;
                        }

                        if (Vector3D.Distance(State.Position.Value, charPos.Value) >
                            SentisOptimisationsPlugin.Config.PlayersSyncDistance)
                        {
                            __result = false;
                            return false;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "AddReplicableToLayerPatched Exception");
            }

            return true;
        }
         
        private static void SendUpdatePatched(MyReplicationServer __instance)
        {
            try
            {
                if (SentisOptimisationsPlugin.Config.PlayersSyncDistance < 0)
                {
                    return;
                }
                var clientDataDict = _clientStates.Invoke(__instance);
                foreach (Object clientState in clientDataDict.Values)
                {
                    object clientData = clientState;

                    MyClientStateBase State = (MyClientStateBase) StateField.GetValue(clientData);
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

                    var clientReplicablesKeys = clientReplicables.Keys;
                    Dictionary<IMyReplicable, Object> replicablesToRemove = new Dictionary<IMyReplicable, object>();
                    
                    foreach (var myReplicable in clientReplicablesKeys)
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
                                replicablesToRemove.Add(myReplicable, clientData);
                                
                            }
                        }
                    }
                    foreach (var keyValuePair in replicablesToRemove)
                    {
                        _removeForClient.Invoke(__instance, keyValuePair.Key, keyValuePair.Value, true); 
                    }
                    
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "SendUpdatePatched Exception");
            }
        }
        
        // private static void GetReplicablesInBoxPatched(MyReplicablesAABB __instance, BoundingBoxD aabb, List<IMyReplicable> list)
        // {
        //     if (SentisOptimisationsPlugin.Config.PlayersSyncDistance < 0)
        //     {
        //         return;
        //     }
        //     List<IMyReplicable> replicablesToFilter = new List<IMyReplicable>();
        //     foreach (var myReplicable in list)
        //     {
        //         var charReplicable = myReplicable as MyEntityReplicableBaseEvent<MyCharacter>;
        //         var charPos = charReplicable?.Instance?.PositionComp?.GetPosition();
        //         if (charPos != null)
        //         {
        //             if (Vector3D.Distance(aabb.Center, charPos.Value) > SentisOptimisationsPlugin.Config.PlayersSyncDistance)
        //             {
        //                 replicablesToFilter.Add(myReplicable);
        //             }
        //         }
        //     }
        //     foreach (var replicableToFilter in replicablesToFilter)
        //     {
        //         list.Remove(replicableToFilter);
        //     }
        // }
        
        private static bool CalculateLayerOfReplicablePatched(Object __instance, IMyReplicable rep, ref Object __result)
        {
            if (SentisOptimisationsPlugin.Config.PlayersSyncDistance < 0)
            {
                return true;
            }
            MyClientStateBase State = (MyClientStateBase) StateField.GetValue(__instance);
            if (!State.Position.HasValue)
            {
                __result = null;
                return false; 
            }
            var charReplicable = rep as MyEntityReplicableBaseEvent<MyCharacter>;
            if (charReplicable != null)
            {
                var isAdmin = PlayerUtils.IsAdmin(PlayerUtils.GetPlayer(State.EndpointId.Id.Value));
                if (isAdmin)
                {
                    return true;
                }
                
                var charPos = charReplicable.Instance?.PositionComp?.GetPosition();
                if (charPos != null)
                {
                    if (State.EndpointId.Id.Value == charReplicable.Instance.ControlSteamId)
                    {
                        return true;
                    }

                    if (Vector3D.Distance(State.Position.Value, charPos.Value) >
                        SentisOptimisationsPlugin.Config.PlayersSyncDistance)
                    {
                        __result = null;
                        return false;
                    }
                }
            }
            return true;
        }
    }
}