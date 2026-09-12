using System;
using System.Collections;
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
        
        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("ReplicablesPatch", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            
            var assembly = typeof(MyReplicationServer).Assembly;
            var MyClientType = assembly.GetType("VRage.Network.MyClient");

            StateField = MyClientType.GetField("State",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            
            // MyClient now has CalculateLayerOfReplicable(rep) and CalculateLayerOfReplicable(rep, Vector3D? secondaryPosition);
            // GetMethod by name throws AmbiguousMatchException, so patch every declared overload.
            var calculateLayerPrefix = typeof(ReplicablesPatch).GetMethod(nameof(CalculateLayerOfReplicablePatched),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var method in MyClientType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.Name != "CalculateLayerOfReplicable") continue;
                ctx.GetPattern(method).Prefixes.Add(calculateLayerPrefix);
            }
            
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
         
        private static bool CalculateLayerOfReplicablePatched(Object __instance, IMyReplicable rep, ref Object __result)
        {
        try
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
                catch (Exception __guard_e)
                {
                    Log.Error("CalculateLayerOfReplicablePatched exception " + __guard_e);
                    return true;  // fall back to vanilla behavior
                }
        }
    }
}