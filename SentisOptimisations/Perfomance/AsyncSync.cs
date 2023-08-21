using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NAPI;
using NLog;
using SentisOptimisationsPlugin.CrashFix;
using Torch.Managers.PatchManager;
using VRage;
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

        public static void Patch(PatchContext ctx)
        {
            var MethodSendStreamingEntry = typeof(MyReplicationServer).GetMethod(
                "SendStreamingEntry",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            ctx.GetPattern(MethodSendStreamingEntry).Prefixes.Add(
                typeof(AsyncSync).GetMethod(nameof(SendStreamingEntryPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MethodFilterStateSync = typeof(MyReplicationServer).GetMethod(
                "FilterStateSync",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            var suppressExceptionFinalizer = typeof(CrashFixPatch).GetMethod(
                nameof(CrashFixPatch.SuppressExceptionFinalizer),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            CrashFixPatch.harmony.Patch(MethodFilterStateSync,
                finalizer: new HarmonyMethod(suppressExceptionFinalizer));

            var MethodRefreshReplicable = typeof(MyReplicationServer).GetMethod(
                "RefreshReplicable",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            CrashFixPatch.harmony.Patch(MethodRefreshReplicable,
                finalizer: new HarmonyMethod(suppressExceptionFinalizer));

            var MethodRemoveClientReplicable = typeof(MyReplicationServer).GetMethod(
                "RemoveClientReplicable",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            CrashFixPatch.harmony.Patch(MethodRemoveClientReplicable,
                finalizer: new HarmonyMethod(suppressExceptionFinalizer));

            var MethodAddClientReplicable = typeof(MyReplicationServer).GetMethod(
                "AddClientReplicable",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            CrashFixPatch.harmony.Patch(MethodAddClientReplicable,
                finalizer: new HarmonyMethod(suppressExceptionFinalizer));
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
                lock (SendReplicablesAsync._queue)
                {
                    SendReplicablesAsync._queue.Enqueue(sendToClientWrapper);
                }
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
                    MyClientStateBase state = (MyClientStateBase) client.easyGetField("State");
                    Endpoint endpointId = state.EndpointId;

                    if (entry.Group.IsProcessingForClient(endpointId) == MyStreamProcessingState.Finished)
                    {
                        var m_callback = (IReplicationServerCallback) __instance.easyGetField("m_callback");
                        MyPacketDataBitStreamBase streamPacketData = m_callback.GetBitStreamPacketData();
                        BitStream stream = streamPacketData.Stream;
                        MyTimeSpan m_serverTimeStamp = (MyTimeSpan) __instance.easyGetField("m_serverTimeStamp");
                        var parameters = new object[] {stream, true, m_serverTimeStamp, null};
                        bool WritePacketHeaderResult = (bool) client.easyCallMethod("WritePacketHeader", parameters);
                        MyTimeSpan clientTimestamp = (MyTimeSpan) parameters[3];
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
                            new object[] {entry.Group, stream, clientTimestamp, 2147483647, true});
                        client.GetType().GetMethod("AddPendingAck", BindingFlags.Instance | BindingFlags.Public)
                            .Invoke(client, new object[] {entry.Group, true});
                        long bitPosition3 = stream.BitPosition;
                        stream.SetBitPositionWrite(bitPosition1);
                        stream.WriteInt32((int) (bitPosition3 - bitPosition2));
                        stream.SetBitPositionWrite(bitPosition3);
                        stream.Terminate();
                        m_callback.SendStateSync(streamPacketData, endpointId, true);
                    }
                    else
                    {
                        client.easyCallMethod("Serialize",
                            new object[] {entry.Group, null, MyTimeSpan.Zero, 2147483647, false});
                        __instance.easyCallMethod("ScheduleStateGroupSync",
                            new[] {client, entry, __instance.easyGetField("SyncFrameCounter"), true});
                    }

                    IMyReplicable owner = entry.Group.Owner;
                    if (owner == null)
                        return;
                    CacheList<IMyReplicable> m_tmpAdd = (CacheList<IMyReplicable>) __instance.easyGetField("m_tmpAdd");
                    using (m_tmpAdd)
                    {
                        MyReplicablesBase m_replicables = (MyReplicablesBase) __instance.easyGetField("m_replicables");
                        m_replicables.GetAllChildren(owner, m_tmpAdd);
                        foreach (IMyReplicable replicable in m_tmpAdd)
                        {
                            bool HasReplicable =
                                (bool) client.easyCallMethod("HasReplicable", new object[] {replicable});
                            if (!HasReplicable)
                                __instance.easyCallMethod("AddForClient",
                                    new[] {replicable, endpointId, client, false, false});
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