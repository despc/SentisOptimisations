using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Havok;
using NAPI;
using NLog;
using NLog.Fluent;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using SentisOptimisations;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Ingame;
using VRage.Library.Collections;
using VRage.Library.Utils;
using VRage.Network;
using VRage.Replication;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class VoxelsPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static HashSet<IMyUpgradeModule> Protectors = null;
        public static FieldInfo fieldm_cachedData = typeof(MyStorageBase)
            .GetField("m_cachedData", BindingFlags.Instance | BindingFlags.NonPublic);
        //id вокселя, тик удаления кэша
        public static ConcurrentDictionary<long, ulong> CacheNeedUpdateDict = new ConcurrentDictionary<long,ulong>();

        public static void Patch(PatchContext ctx)
        {
            var MethodMakeCraterInternal = typeof(MyVoxelGenerator).GetMethod(
                "MakeCraterInternal",
                BindingFlags.Static | BindingFlags.NonPublic);

            ctx.GetPattern(MethodMakeCraterInternal).Prefixes.Add(
                typeof(VoxelsPatch).GetMethod(nameof(PatchMakeCraterInternal),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodCutOutShapeWithProperties = typeof(MyVoxelGenerator).GetMethod(
                "CutOutShapeWithProperties",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            ctx.GetPattern(MethodCutOutShapeWithProperties).Prefixes.Add(
                typeof(VoxelsPatch).GetMethod(nameof(PatchCutOutShape),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodCutOutShapeWithPropertiesAsync = typeof(MyVoxelBase).GetMethod(
                "CutOutShapeWithPropertiesAsync",
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            ctx.GetPattern(MethodCutOutShapeWithPropertiesAsync).Prefixes.Add(
                typeof(VoxelsPatch).GetMethod(nameof(PatchCutOutShape),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var type = typeof(MyVoxelBase).Assembly.GetType("Sandbox.Game.MyExplosion");

            var MethodCCutOutVoxelMap = type.GetMethod(
                "CutOutVoxelMap",
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            
            ctx.GetPattern(MethodCCutOutVoxelMap).Prefixes.Add(
                typeof(VoxelsPatch).GetMethod(nameof(PatchCutOutVoxelMap),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            
            var MethodCutOutShape = typeof(MyVoxelGenerator).GetMethod(
                "CutOutShape",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            ctx.GetPattern(MethodCutOutShape).Prefixes.Add(
                typeof(VoxelsPatch).GetMethod(nameof(PatchCutOutShape),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodBreakLogicHandler = typeof(MyGridPhysics).GetMethod(
                "BreakLogicHandler",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            ctx.GetPattern(MethodBreakLogicHandler).Prefixes.Add(
                typeof(VoxelsPatch).GetMethod(nameof(PatchBreakLogicHandler),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodRequestCutOut = typeof(MyShipMiningSystem).GetMethod(
                "RequestCutOut",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            ctx.GetPattern(MethodRequestCutOut).Prefixes.Add(
                typeof(VoxelsPatch).GetMethod(nameof(PatchRequestCutOut),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodSendStreamingEntry = typeof(MyReplicationServer).GetMethod(
                "SendStreamingEntry",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            ctx.GetPattern(MethodSendStreamingEntry).Prefixes.Add(
                typeof(VoxelsPatch).GetMethod(nameof(SendStreamingEntryPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }
        private static bool SendStreamingEntryPatched(MyReplicationServer __instance, 
            Object client, MyStateDataEntry entry)
        {
            if (!SentisOptimisationsPlugin.Config.FixVoxelFreeze)
            {
                return true;
            }

            try
            {
                var sendToClientWrapper = new SendToClientWrapper(__instance, client, entry);
                SendReplicablesAsync._queue.Enqueue(sendToClientWrapper);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
            return false;
        }

        public class SendToClientWrapper
        {
            private MyReplicationServer __instance;
            private object client;
            private MyStateDataEntry entry;

            public SendToClientWrapper(MyReplicationServer instance, object client, MyStateDataEntry entry)
            {
                __instance = instance;
                this.client = client;
                this.entry = entry;
            }

            public void DoSendToClient()
            {
                try
                {
                    MyClientStateBase state = (MyClientStateBase)client.easyGetField("State");
                    Endpoint endpointId = state.EndpointId;

                    if (entry.Group.IsProcessingForClient(endpointId) == MyStreamProcessingState.Finished)
                    {
                        var m_callback = ((IReplicationServerCallback)__instance.easyGetField("m_callback"));
                        MyPacketDataBitStreamBase streamPacketData = m_callback.GetBitStreamPacketData();
                        BitStream stream = streamPacketData.Stream;
                        MyTimeSpan m_serverTimeStamp = (MyTimeSpan)__instance.easyGetField("m_serverTimeStamp");
                        var parameters = new object[] { stream, true, m_serverTimeStamp, null };
                        bool WritePacketHeaderResult = (bool)client.easyCallMethod("WritePacketHeader", parameters);
                        MyTimeSpan clientTimestamp = (MyTimeSpan)parameters[3];
                        if (!WritePacketHeaderResult)
                        {
                            streamPacketData.Return();
                            return;
                        }

                        stream.Terminate();
                        stream.WriteNetworkId(entry.GroupId);
                        long bitPosition1 = stream.BitPosition;
                        stream.WriteInt32(0);
                        long bitPosition2 = stream.BitPosition;
                        client.easyCallMethod("Serialize",
                            new object[] { entry.Group, stream, clientTimestamp, 2147483647, true });
                        client.GetType().GetMethod("AddPendingAck", BindingFlags.Instance | BindingFlags.Public)
                            .Invoke(client, new object[] { entry.Group, true });
                        long bitPosition3 = stream.BitPosition;
                        stream.SetBitPositionWrite(bitPosition1);
                        stream.WriteInt32((int)(bitPosition3 - bitPosition2));
                        stream.SetBitPositionWrite(bitPosition3);
                        stream.Terminate();
                        m_callback.SendStateSync((IPacketData)streamPacketData, endpointId, true);
                    }
                    else
                    {
                        client.easyCallMethod("Serialize",
                            new object[] { entry.Group, (BitStream)null, MyTimeSpan.Zero, 2147483647, false });
                        __instance.easyCallMethod("ScheduleStateGroupSync",
                            new object[] { client, entry, __instance.easyGetField("SyncFrameCounter"), true });
                    }

                    IMyReplicable owner = entry.Group.Owner;
                    if (owner == null)
                        return;
                    CacheList<IMyReplicable> m_tmpAdd = (CacheList<IMyReplicable>)__instance.easyGetField("m_tmpAdd");
                    using (m_tmpAdd)
                    {
                        MyReplicablesBase m_replicables = (MyReplicablesBase)__instance.easyGetField("m_replicables");
                        m_replicables.GetAllChildren(owner, (List<IMyReplicable>)m_tmpAdd);
                        foreach (IMyReplicable replicable in (List<IMyReplicable>)m_tmpAdd)
                        {
                            bool HasReplicable =
                                (bool)client.easyCallMethod("HasReplicable", new object[] { replicable });
                            if (!HasReplicable)
                                __instance.easyCallMethod("AddForClient",
                                    new object[] { replicable, endpointId, client, false, false });
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Error("Send to client exception ", e);
                }
            }
        }
        


        private static bool PatchBreakLogicHandler(HkRigidBody otherBody, MyGridPhysics __instance,
            ref HkBreakOffLogicResult __result)
        {
            try
            {
                if (Protectors == null)
                {
                    return true;
                }

                var pos = ((MyCubeGrid)__instance.easyGetField("m_grid")).PositionComp.GetPosition();

                foreach (var myUpgradeModule in Protectors)
                {
                    if (Vector3D.Distance(myUpgradeModule.PositionComp.GetPosition(), pos) < 300)
                    {
                        if (myUpgradeModule.Enabled && myUpgradeModule.IsFunctional)
                        {
                            IMyEntity entity1 = otherBody.GetEntity(0U);
                            if (entity1 is MyVoxelBase)
                            {
                                __result = HkBreakOffLogicResult.DoNotBreakOff;
                                return false;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }

            return true;
        }

        private static bool PatchMakeCraterInternal(BoundingSphereD sphere)
        {
            try
            {
                var pos = sphere.Center;
                if (Protectors == null)
                {
                    return true;
                }

                foreach (var myUpgradeModule in Protectors)
                {
                    if (Vector3D.Distance(myUpgradeModule.PositionComp.GetPosition(), pos) < 300)
                    {
                        if (myUpgradeModule.Enabled && myUpgradeModule.IsFunctional)
                        {
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }

            return true;
        }
        
        private static bool PatchRequestCutOut(Vector3D hitPosition)
        {
            try
            {
                var pos = hitPosition;
                if (Protectors == null)
                {
                    return true;
                }

                foreach (var myUpgradeModule in Protectors)
                {
                    if (Vector3D.Distance(myUpgradeModule.PositionComp.GetPosition(), pos) < 300)
                    {
                        if (myUpgradeModule.Enabled && myUpgradeModule.IsFunctional)
                        {
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }

            return true;
        }
        

        private static bool PatchCutOutVoxelMap(Vector3D center)
        {
            try
            {
                var pos = center;
                if (Protectors == null)
                {
                    return true;
                }

                foreach (var myUpgradeModule in Protectors)
                {
                    if (Vector3D.Distance(myUpgradeModule.PositionComp.GetPosition(), pos) < 300)
                    {
                        if (myUpgradeModule.Enabled && myUpgradeModule.IsFunctional)
                        {
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }

            return true;
        }
        
        private static bool PatchCutOutShape(MyShape shape)
        {
            try
            {
                var pos = shape.GetWorldBoundaries().Center;
                if (Protectors == null)
                {
                    return true;
                }

                foreach (var myUpgradeModule in Protectors)
                {
                    if (Vector3D.Distance(myUpgradeModule.PositionComp.GetPosition(), pos) < 300)
                    {
                        if (myUpgradeModule.Enabled && myUpgradeModule.IsFunctional)
                        {
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }

            return true;
        }
    }
}