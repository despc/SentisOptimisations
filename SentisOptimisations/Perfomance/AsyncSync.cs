using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using NAPI;
using NLog;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Collections;
using VRage.Library;
using VRage.Library.Collections;
using VRage.Library.Utils;
using VRage.Network;
using VRage.Replication;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class AsyncSync
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static MethodInfo MethodScheduleStateGroupSync =  typeof(MyReplicationServer).getMethod("ScheduleStateGroupSync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static MethodInfo MethodSendStateSync = null;
        
        public static void Patch(PatchContext ctx)
        {
            var MethodSendStreamingEntry = typeof(MyReplicationServer).GetMethod(
                "SendStreamingEntry",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            ctx.GetPattern(MethodSendStreamingEntry).Prefixes.Add(
                typeof(AsyncSync).GetMethod(nameof(SendStreamingEntryPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            // var MethodApplyDirtyGroups = typeof(MyReplicationServer).GetMethod(
            //     "ApplyDirtyGroups",
            //     BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            // ctx.GetPattern(MethodApplyDirtyGroups).Prefixes.Add(
            //     typeof(AsyncSync).GetMethod(nameof(ApplyDirtyGroupsPatched),
            //         BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            //
            // var MethodFilterStateSync = typeof(MyReplicationServer).GetMethod(
            //     "FilterStateSync",
            //     BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            // ctx.GetPattern(MethodFilterStateSync).Prefixes.Add(
            //     typeof(AsyncSync).GetMethod(nameof(FilterStateSyncPatched),
            //         BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            //
            // var assembly = typeof(MyReplicationServer).Assembly;
            // var myClientType = assembly.GetType("VRage.Network.MyClient");
            // MethodSendStateSync = myClientType.getMethod("SendStateSync", BindingFlags.Instance | BindingFlags.Public);
        }


        private static bool FilterStateSyncPatched(MyReplicationServer __instance, Object client)
        {
            if (!SentisOptimisationsPlugin.Config.AsyncSync)
            {
                return true;
            }

            try
            {
                var isAckAvailable = (bool)client.easyCallMethod("IsAckAvailable", new Object[] { });
                if (!isAckAvailable)
                    return false;
                __instance.easyCallMethod("ApplyDirtyGroups", new Object[] { });
                int num1 = 0;
                MyPacketDataBitStreamBase data = (MyPacketDataBitStreamBase)null;
                List<MyStateDataEntry> myStateDataEntryList = PoolManager.Get<List<MyStateDataEntry>>();
                IReplicationServerCallback m_callback =
                    (IReplicationServerCallback)__instance.easyGetField("m_callback");
                int mtuSize = m_callback.GetMTUSize();
                FastPriorityQueue<MyStateDataEntry> DirtyQueue =
                    (FastPriorityQueue<MyStateDataEntry>)client.easyGetField("DirtyQueue");
                int count = DirtyQueue.Count;
                int num2 = 7;
                MyStateDataEntry entry = (MyStateDataEntry)null;
                var SyncFrameCounter = (long)__instance.easyGetField("SyncFrameCounter");
                try
                {
                    var dirtyEntity = DirtyQueue.First;
                    if (dirtyEntity == null)
                    {
                        return false;
                    }
                    while (count-- > 0 && num2 > 0 &&
                           (long)dirtyEntity.easyGetField("Priority") < SyncFrameCounter)
                    {
                        MyStateDataEntry stateGroupEntry = DirtyQueue.Dequeue();
                        myStateDataEntryList.Add(stateGroupEntry);
                        MyReplicableClientData replicableClientData;
                        MyConcurrentDictionary<IMyReplicable, MyReplicableClientData> Replicables =
                            (MyConcurrentDictionary<IMyReplicable, MyReplicableClientData>)client.easyGetField(
                                "Replicables");
                        if (stateGroupEntry.Owner == null || stateGroupEntry.Group.IsStreaming ||
                            Replicables.TryGetValue(stateGroupEntry.Owner, out replicableClientData) &&
                            replicableClientData.HasActiveStateSync)
                        {
                            if (stateGroupEntry.Group.IsStreaming)
                            {
                                MyClientStateBase State = (MyClientStateBase)client.easyGetField("State");
                                if (entry == null && stateGroupEntry.Group.IsProcessingForClient(State.EndpointId) !=
                                    MyStreamProcessingState.Processing)
                                    entry = stateGroupEntry;
                                continue;
                            }

                            var args = new object[]
                            {
                                stateGroupEntry, mtuSize, data,
                                (MyTimeSpan)__instance.easyGetField("m_serverTimeStamp")
                            };
                            var sendUpdateSyncResult = (bool)client.easyCallMethod("SendStateSync", args);
                            data = (MyPacketDataBitStreamBase)args[2];
                            if (sendUpdateSyncResult)
                            {
                                ++num1;
                                if (data == null)
                                    --num2;
                                continue;
                            }

                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Error("Process DirtyQueue exception", e);
                }


                if (data != null)
                {
                    data.Stream.Terminate();
                    MyClientStateBase State = (MyClientStateBase)client.easyGetField("State");
                    m_callback = (IReplicationServerCallback)__instance.easyGetField("m_callback");
                    m_callback.SendStateSync((IPacketData)data, State.EndpointId, false);
                }

                if (entry != null)
                    __instance.easyCallMethod("SendStreamingEntry", new object[] { client, entry });
                long syncFrameCounter = SyncFrameCounter;
                foreach (MyStateDataEntry groupEntry in myStateDataEntryList)
                {
                    MyClientStateBase State = (MyClientStateBase)client.easyGetField("State");
                    Dictionary<IMyStateGroup, MyStateDataEntry> StateGroups =
                        (Dictionary<IMyStateGroup, MyStateDataEntry>)client.easyGetField("StateGroups");
                    if (StateGroups.ContainsKey(groupEntry.Group) &&
                        groupEntry.Group.IsStillDirty(State.EndpointId))
                    {
                        MethodScheduleStateGroupSync.Invoke(__instance,
                            new object[] { client, groupEntry, syncFrameCounter, true });
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("FilterStateSync exception", e);
            }

            return false;
        }
        

        private static bool ApplyDirtyGroupsPatched(MyReplicationServer __instance)
        {
            if (!SentisOptimisationsPlugin.Config.AsyncSync)
            {
                return true;
            }

            try
            {
                ProcessDirtyGroups(__instance);
            }
            catch (Exception e)
            {
                Log.Error("ApplyDirtyGroups exception", e);
            }
            

            return false;
        }

        private static void ProcessDirtyGroups(MyReplicationServer __instance)
        {
            var sendToClientWrapper2 = new SendToClientWrapper2(__instance);
            SendReplicablesAsync._queue.Enqueue(sendToClientWrapper2);
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

        public interface ISendToClientWrapper
        {
            public void DoSendToClient();
        }

        public class SendToClientWrapper2 : ISendToClientWrapper
        {
            private MyReplicationServer __instance;
            // private IEnumerable m_clientStates;
            // private IMyStateGroup result;
            // private long syncFrameCounter;

            public SendToClientWrapper2(MyReplicationServer instance)
            {
                __instance = instance;
            }

            public void DoSendToClient()
            {
                try
                {
                    IMyStateGroup result;
                    IEnumerable m_clientStates =
                        (IEnumerable)__instance.easyGetField(
                            "m_clientStates"); // ConcurrentDictionary<Endpoint, MyClient>
                    long syncFrameCounter = (long)__instance.easyGetField("SyncFrameCounter");
                    ConcurrentQueue<IMyStateGroup> m_dirtyGroups =
                        (ConcurrentQueue<IMyStateGroup>)__instance.easyGetField("m_dirtyGroups");
                    while (m_dirtyGroups.TryDequeue(out result)) // И тут дохуя грязных групп
                    {
                        foreach (object clientState in m_clientStates) // перебор по коллекции долгий
                        {
                            var client = clientState.easyGetField("value");
                            MyStateDataEntry groupEntry;
                            Dictionary<IMyStateGroup, MyStateDataEntry> StateGroups =
                                (Dictionary<IMyStateGroup, MyStateDataEntry>)client.easyGetField("StateGroups");

                            if (StateGroups.TryGetValue(result, out groupEntry)) // Долгое доставание
                            {
                                MethodScheduleStateGroupSync.Invoke(__instance,
                                    new object[] { client, groupEntry, syncFrameCounter, true });
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Error("Send to client 2 exception ", e);
                }
            }
        }

        public class SendToClientWrapper : ISendToClientWrapper
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
    }
}