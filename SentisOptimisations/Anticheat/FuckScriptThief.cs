using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Serialization;
using NLog;
using ParallelTasks;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Replication;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using SentisOptimisations;
using Torch;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Library.Collections;
using VRage.Network;
using VRage.ObjectBuilders;
using VRage.Serialization;
using VRage.Utils;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public class FuckScriptThief
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static Type MyCubeGridReplicableType =
            typeof(MyTerminalBlock).Assembly.GetType("Sandbox.Game.Replication.MyCubeGridReplicable");
        private static PropertyInfo gridPropertyInfo = MyCubeGridReplicableType.GetProperty("Grid", BindingFlags.Instance | BindingFlags.NonPublic);
        public static void Patch(PatchContext ctx)
        {
            

            // MyCubeGridReplicable.Serialize
            var Serialize = MyCubeGridReplicableType.GetMethod("Serialize",BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
               ;
            ctx.GetPattern(Serialize).Prefixes.Add(
                typeof(FuckScriptThief).GetMethod(nameof(SerializePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
         
            
        }

        private static bool SerializePatched(MyExternalReplicable __instance, BitStream stream,
            HashSet<string> cachedData,
            Endpoint forClient,
            Action writeData)
        {
            MyCubeGrid Grid = (MyCubeGrid)gridPropertyInfo.GetValue(__instance);
            if (Grid.Closed)
                return false;
            
            var player = MySession.Static.Players.TryGetPlayerBySteamId(forClient.Id.Value);
            if (player == null || player.Identity == null)
            {
                Log.Error("cant replicate - player null");
                return false;
            }
            
            var requestFromIdentity = player.Identity.IdentityId;

            if (PlayerUtils.IsAdmin(requestFromIdentity))
            {
                return true;
            }
            var owner = PlayerUtils.GetOwner(Grid);
            if (owner == 0)
            {
                return true;
            }

            var myRelationsBetweenPlayers = MyIDModule.GetRelationPlayerPlayer(requestFromIdentity, owner);
            if (myRelationsBetweenPlayers == MyRelationsBetweenPlayers.Allies || myRelationsBetweenPlayers == MyRelationsBetweenPlayers.Self)
            {
                return true;
            }
            
            stream.WriteBool(false);
            MyObjectBuilder_EntityBase builder;
            using (MyReplicationLayer.StartSerializingReplicable((IMyReplicable) __instance, forClient))
                builder = Grid.GetObjectBuilder(false);
            MyReplicationServer replicationServer = (MyReplicationServer)ReflectionUtils.InvokeStaticMethod(typeof(MyMultiplayer), "GetReplicationServer", new object[]{});
            double time = replicationServer.GetClientRelevantServerTimestamp(forClient).Milliseconds;
            Parallel.Start((Action) (() =>
            {
                try
                {
                    foreach (var myObjectBuilderCubeBlock in ((MyObjectBuilder_CubeGrid)builder).CubeBlocks)
                    {
                        if (myObjectBuilderCubeBlock is MyObjectBuilder_MyProgrammableBlock)
                        {
                            ((MyObjectBuilder_MyProgrammableBlock)myObjectBuilderCubeBlock).Program =
                                "You don't need to see this";
                        }
                    }
                    MySerializer.Write<MyObjectBuilder_EntityBase>(stream, ref builder, MyObjectBuilderSerializer.Dynamic);
                }
                catch (Exception ex)
                {
                    XmlSerializer serializer = MyXmlSerializerManager.GetSerializer(builder.GetType());
                    MyLog.Default.WriteLine("Grid data - START");
                    try
                    {
                        serializer.Serialize(MyLog.Default.GetTextWriter(), (object) builder);
                    }
                    catch
                    {
                        MyLog.Default.WriteLine("Failed");
                    }
                    MyLog.Default.WriteLine("Grid data - END");
                    throw;
                }
                stream.WriteDouble(time);
                writeData();
            }));
            
            return false;
        }
        
        private static bool SetValueLimitedPatchedStorage(String value)
        {
            if (value.Length > 100240)
            {
                Log.Error("Storage TOO LONG " + value);
                Thread.Sleep(15);
                return false;
            }
            return true;
        }
        
        private static bool SetValueLimitedPatched(String value)
        {
            if (value.Length > 100240)
            {
                Log.Error("CustomData TOO LONG " + value);
                Thread.Sleep(15);
                return false;
            }
            return true;
        }
    }
}