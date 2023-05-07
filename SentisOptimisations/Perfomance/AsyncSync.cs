using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NAPI;
using NLog;
using SentisOptimisationsPlugin.CrashFix;
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
            //
            // var MethodSendDirtyBlockLimit = typeof(MyPlayerCollection).GetMethod(
            //     nameof(MyPlayerCollection.SendDirtyBlockLimit),
            //     BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            // ctx.GetPattern(MethodSendDirtyBlockLimit).Prefixes.Add(
            //     typeof(AsyncSync).GetMethod(nameof(SendDirtyBlockLimitPatched),
            //         BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
                
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
            var MethodFilterStateSync = typeof(MyReplicationServer).GetMethod(
                "FilterStateSync",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var finalizer = typeof(CrashFixPatch).GetMethod(nameof(CrashFixPatch.SuppressExceptionFinalizer),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            CrashFixPatch.harmony.Patch(MethodFilterStateSync, finalizer: new HarmonyMethod(finalizer));
            
            var MethodRefreshReplicable = typeof(MyReplicationServer).GetMethod(
                "RefreshReplicable",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            CrashFixPatch.harmony.Patch(MethodRefreshReplicable, finalizer: new HarmonyMethod(finalizer));
            
            var MethodRemoveClientReplicable = typeof(MyReplicationServer).GetMethod(
                "RemoveClientReplicable",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            CrashFixPatch.harmony.Patch(MethodRemoveClientReplicable, finalizer: new HarmonyMethod(finalizer));
            // ctx.GetPattern(MethodFilterStateSync).Prefixes.Add(
            //     typeof(AsyncSync).GetMethod(nameof(FilterStateSyncPatched),
            //         BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            //
            // var assembly = typeof(MyReplicationServer).Assembly;
            // var myClientType = assembly.GetType("VRage.Network.MyClient");
            // MethodSendStateSync = myClientType.getMethod("SendStateSync", BindingFlags.Instance | BindingFlags.Public);
            
            // var assembly = typeof(MyCubeGrid).Assembly;
            // var MyEntityInventoryStateGroupType = assembly.GetType("Sandbox.Game.Replication.StateGroups.MyEntityInventoryStateGroup");

        }
        
        // private static bool CalculateAddsAndRemovalsPatched(Object __instance, Object clientData,
        //     ref Object delta,
        //     List<MyPhysicalInventoryItem> items)
        // {
        //     if (!SentisOptimisationsPlugin.Config.AsyncSync)
        //     {
        //         return true;
        //     }
        //     
        //     delta = new MyEntityInventoryStateGroup.InventoryDeltaInformation()
        //     {
        //         HasChanges = false
        //     };
        //     int key = 0;
        //     foreach (MyPhysicalInventoryItem physicalInventoryItem in items)
        //     {
        //         MyEntityInventoryStateGroup.ClientInvetoryData clientInvetoryData;
        //         if (clientData.ClientItemsSorted.TryGetValue(physicalInventoryItem.ItemId, out clientInvetoryData))
        //         {
        //             if (clientInvetoryData.Item.Content.TypeId == physicalInventoryItem.Content.TypeId && clientInvetoryData.Item.Content.SubtypeId == physicalInventoryItem.Content.SubtypeId)
        //             {
        //                 this.m_foundDeltaItems.Add(physicalInventoryItem.ItemId);
        //                 MyFixedPoint myFixedPoint1 = physicalInventoryItem.Amount;
        //                 if (physicalInventoryItem.Content is MyObjectBuilder_GasContainerObject content)
        //                     myFixedPoint1 = (MyFixedPoint) content.GasLevel;
        //                 if (clientInvetoryData.Amount != myFixedPoint1)
        //                 {
        //                     MyFixedPoint myFixedPoint2 = myFixedPoint1 - clientInvetoryData.Amount;
        //                     if (delta.ChangedItems == null)
        //                         delta.ChangedItems = new Dictionary<uint, MyFixedPoint>();
        //                     delta.ChangedItems[physicalInventoryItem.ItemId] = myFixedPoint2;
        //                     delta.HasChanges = true;
        //                 }
        //             }
        //         }
        //         else
        //         {
        //             if (delta.NewItems == null)
        //                 delta.NewItems = new SortedDictionary<int, MyPhysicalInventoryItem>();
        //             delta.NewItems[key] = physicalInventoryItem;
        //             delta.HasChanges = true;
        //         }
        //         ++key;
        //     }
        //     foreach (KeyValuePair<uint, MyEntityInventoryStateGroup.ClientInvetoryData> keyValuePair in clientData.ClientItemsSorted)
        //     {
        //         if (delta.RemovedItems == null)
        //             delta.RemovedItems = new List<uint>();
        //         if (!this.m_foundDeltaItems.Contains(keyValuePair.Key))
        //         {
        //             delta.RemovedItems.Add(keyValuePair.Key);
        //             delta.HasChanges = true;
        //         }
        //     }
        // }

        // private static bool SendDirtyBlockLimitPatched(MyPlayerCollection __instance, MyBlockLimits blockLimit,
        //     List<EndpointId> playersToSendTo)
        // {
        //     foreach (MyBlockLimits.MyTypeLimitData myTypeLimitData in
        //              (IEnumerable<MyBlockLimits.MyTypeLimitData>)blockLimit.BlockTypeBuilt.Values)
        //     {
        //         if (Interlocked.CompareExchange(ref myTypeLimitData.Dirty, 0, 1) > 0)
        //         {
        //             foreach (EndpointId targetEndpoint in playersToSendTo)
        //                 MyMultiplayer.RaiseStaticEvent<MyBlockLimits.MyTypeLimitData>(
        //                     (Func<IMyEventOwner, Action<MyBlockLimits.MyTypeLimitData>>)(x =>
        //                     {
        //                         var action = new Action<MyBlockLimits.MyTypeLimitData>(
        //                             delegate(MyBlockLimits.MyTypeLimitData data)
        //                             {
        //                                 MyPlayerCollection.SetIdentityBlockTypesBuilt(data);
        //                             });
        //                         return action;
        //                     }), myTypeLimitData, targetEndpoint);
        //         }
        //     }
        //
        //     foreach (MyBlockLimits.MyGridLimitData myGridLimitData in
        //              (IEnumerable<MyBlockLimits.MyGridLimitData>)blockLimit.BlocksBuiltByGrid.Values)
        //     {
        //         if (Interlocked.CompareExchange(ref myGridLimitData.Dirty, 0, 1) > 0)
        //         {
        //             foreach (EndpointId targetEndpoint in playersToSendTo)
        //                 MyMultiplayer.RaiseStaticEvent<MyBlockLimits.MyGridLimitData, int, int, int, int>(
        //                     (Func<IMyEventOwner, Action<MyBlockLimits.MyGridLimitData, int, int, int, int>>)(x =>
        //                         new Action<MyBlockLimits.MyGridLimitData, int, int, int, int>(MyPlayerCollection
        //                             .SetIdentityGridBlocksBuilt)), myGridLimitData, blockLimit.PCU, blockLimit.PCUBuilt,
        //                     blockLimit.BlocksBuilt, blockLimit.TransferedDelta, targetEndpoint);
        //         }
        //
        //         if (Interlocked.CompareExchange(ref myGridLimitData.NameDirty, 0, 1) > 0)
        //         {
        //             foreach (EndpointId targetEndpoint in playersToSendTo)
        //                 MyMultiplayer.RaiseStaticEvent<long, string>(
        //                     (Func<IMyEventOwner, Action<long, string>>)(x =>
        //                         new Action<long, string>(MyBlockLimits.SetGridNameFromServer)),
        //                     myGridLimitData.EntityId, myGridLimitData.GridName, targetEndpoint);
        //         }
        //     }
        //
        //     if (blockLimit.CompareExchangePCUDirty())
        //     {
        //         foreach (EndpointId targetEndpoint in playersToSendTo)
        //             MyMultiplayer.RaiseStaticEvent<int, int>(
        //                 (Func<IMyEventOwner, Action<int, int>>)(x =>
        //                     new Action<int, int>(MyPlayerCollection.SetPCU_Client)), blockLimit.PCU,
        //                 blockLimit.TransferedDelta, targetEndpoint);
        //     }
        //
        //     label_36:
        //     long key;
        //     MyBlockLimits.MyGridLimitData myGridLimitData1;
        //     do
        //     {
        //         key = blockLimit.GridsRemoved.Keys.ElementAtOrDefault<long>(0);
        //         if (key == 0L)
        //             goto label_42;
        //     } while (!blockLimit.GridsRemoved.TryRemove(key, out myGridLimitData1));
        //
        //     goto label_38;
        //     label_42:
        //     return;
        //     label_38:
        //     using (List<EndpointId>.Enumerator enumerator = playersToSendTo.GetEnumerator())
        //     {
        //         while (enumerator.MoveNext())
        //         {
        //             EndpointId current = enumerator.Current;
        //             MyMultiplayer.RaiseStaticEvent<MyBlockLimits.MyGridLimitData, int, int, int, int>(
        //                 (Func<IMyEventOwner, Action<MyBlockLimits.MyGridLimitData, int, int, int, int>>)(x =>
        //                     new Action<MyBlockLimits.MyGridLimitData, int, int, int, int>(MyPlayerCollection
        //                         .SetIdentityGridBlocksBuilt)), myGridLimitData1, blockLimit.PCU, blockLimit.PCUBuilt,
        //                 blockLimit.BlocksBuilt, blockLimit.TransferedDelta, current);
        //         }
        //
        //         goto label_36;
        //     }
        // }

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
            catch (ArgumentException ex)
            {
                Log.Error("ArgumentException " + SendReplicablesAsync._queue.Count(), ex);
                Log.Error("Clean async queue ");
                SendReplicablesAsync._queue.Clear();
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