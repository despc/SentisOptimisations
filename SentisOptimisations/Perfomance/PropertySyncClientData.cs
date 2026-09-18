using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using VRage.Library.Collections;
using VRage.Network;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Compact per-client data of MyPropertySyncStateGroup (terminal properties of blocks).
    ///
    /// Vanilla keeps, for every property group and every client that has it replicated, a
    /// DataPerClient with <c>SentProperties = new SmallBitField[256]</c>: the properties sent in each
    /// state sync packet id, so they can be marked dirty again if that packet is lost. That is ~2 KB
    /// per block per client; with tens of thousands of terminal blocks in sync range every joining
    /// player allocates over a hundred megabytes, which then lives in the old GC generations as long
    /// as the player stays.
    ///
    /// Only packets that are still waiting for their ack matter, and a group is in very few of them
    /// at a time. The array is replaced by a small growable table of (packet id, sent bits) pairs,
    /// stored in the same SmallBitField[] field: even slots hold packet id + 1 (0 = free), odd slots
    /// the sent bits. The entry is written where vanilla writes SentProperties[packetId] and taken
    /// (read and freed) when the ack for that packet arrives, delivered or not.
    ///
    /// Behaviour is the same as vanilla, including re-marking the properties of a lost packet dirty.
    /// The one vanilla quirk not kept: a group that did not fit into a packet is reported lost for
    /// that packet id, and vanilla then ORs in whatever an older packet with the same id had sent;
    /// here nothing is added (its dirty bits were never cleared, so nothing is lost).
    /// </summary>
    [PatchShim]
    public static class PropertySyncClientData
    {
        public const int InitialEntries = 4;
        public const int MaxEntries = 256;

        private static readonly Type GroupType = typeof(MyCubeGrid).Assembly
            .GetType("Sandbox.Game.Replication.StateGroups.MyPropertySyncStateGroup", true);
        private static readonly Type ServerDataType = GroupType.GetNestedType("ServerData", BindingFlags.NonPublic);
        private static readonly Type DataPerClientType = ServerDataType.GetNestedType("DataPerClient", BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo SentPropertiesField = DataPerClientType.GetField("SentProperties");
        private static readonly FieldInfo DirtyPropertiesField = DataPerClientType.GetField("DirtyProperties");
        private static readonly FieldInfo ServerDataField = GroupType.GetField("m_serverData", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ServerClientDataField = ServerDataType.GetField("ServerClientData");
        private static readonly FieldInfo PropertiesField = GroupType.GetField("m_properties", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly Func<object, SmallBitField[]> GetSent = BuildGetter<SmallBitField[]>(DataPerClientType, SentPropertiesField);
        private static readonly Action<object, SmallBitField[]> SetSent = BuildSetter<SmallBitField[]>(DataPerClientType, SentPropertiesField);
        private static readonly Func<object, Endpoint, object> GetClientData = BuildClientDataGetter();
        private static readonly Action<object, Endpoint, object> AddClientData = BuildClientDataAdder();
        private static readonly Func<object, int> PropertyCount = BuildPropertyCount();
        private static readonly Func<object, ulong> GetDirty = BuildDirtyGetter();
        private static readonly Action<object, ulong> SetDirty = BuildDirtySetter();

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("PropertySyncClientData", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var self = typeof(PropertySyncClientData);
            ctx.GetPattern(GroupType.GetMethod("CreateClientData", instance))
                .Prefixes.Add(self.GetMethod(nameof(CreateClientDataPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            ctx.GetPattern(GroupType.GetMethod("OnAck", instance))
                .Prefixes.Add(self.GetMethod(nameof(OnAckPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            ctx.GetPattern(GroupType.GetMethod("Serialize", instance))
                .Transpilers.Add(self.GetMethod(nameof(SerializeTranspiler), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool CreateClientDataPrefix(object __instance, MyClientStateBase forClient)
        {
            // No constructor: it would allocate the 256-entry array.
            var data = FormatterServices.GetUninitializedObject(DataPerClientType);
            SetSent(data, new SmallBitField[InitialEntries * 2]);
            AddClientData(__instance, forClient.EndpointId, data);
            if (PropertyCount(__instance) > 0) SetDirty(data, SmallBitField.BitsFull);
            return false;
        }

        private static bool OnAckPrefix(object __instance, MyClientStateBase forClient, byte packetId, bool delivered)
        {
            var data = GetClientData(__instance, forClient.EndpointId);
            var sent = Take(GetSent(data), packetId);
            if (!delivered)
            {
                SetDirty(data, GetDirty(data) | sent);
                ((MyReplicationServer)MyMultiplayer.Static.ReplicationLayer).AddToDirtyGroups((IMyStateGroup)__instance);
            }
            return false;
        }

        /// <summary>
        /// Replaces <c>data.SentProperties[packetId]</c> (ldfld SentProperties; ldarg packetId; ldelema)
        /// with <c>SentSlot(data, packetId)</c>.
        /// </summary>
        private static IEnumerable<MsilInstruction> SerializeTranspiler(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            var slot = typeof(PropertySyncClientData).GetMethod(nameof(SentSlot), BindingFlags.Static | BindingFlags.Public);
            var replaced = 0;
            for (var i = 0; i + 2 < list.Count; i++)
            {
                if (list[i].OpCode != OpCodes.Ldfld || !(list[i].Operand is MsilOperandInline<FieldInfo> field) || field.Value != SentPropertiesField)
                    continue;
                if (list[i + 2].OpCode != OpCodes.Ldelema)
                    throw new InvalidOperationException("unexpected IL after SentProperties in MyPropertySyncStateGroup.Serialize");
                var call = new MsilInstruction(OpCodes.Call).InlineValue(slot);
                foreach (var label in list[i + 2].Labels) call.Labels.Add(label);
                foreach (var label in list[i].Labels) list[i + 1].Labels.Add(label);
                list[i + 2] = call;
                list.RemoveAt(i);
                replaced++;
            }
            if (replaced != 1)
                throw new InvalidOperationException("expected one SentProperties access in MyPropertySyncStateGroup.Serialize, found " + replaced);
            return list;
        }

        /// <summary>The sent-bits slot for a packet id, added if missing (grows the table when full).</summary>
        public static ref SmallBitField SentSlot(object data, byte packetId)
        {
            var table = GetSent(data);
            var index = FindOrAdd(ref table, packetId, out var grown);
            if (grown) SetSent(data, table);
            return ref table[index];
        }

        public static int FindOrAdd(ref SmallBitField[] table, byte packetId, out bool grown)
        {
            grown = false;
            var key = packetId + 1UL;
            var free = -1;
            for (var i = 0; i < table.Length; i += 2)
            {
                if (table[i].Bits == key) return i + 1;
                if (free < 0 && table[i].Bits == 0) free = i;
            }
            if (free < 0)
            {
                free = table.Length;
                Array.Resize(ref table, Math.Min(table.Length * 2, MaxEntries * 2));
                grown = true;
            }
            table[free].Bits = key;
            table[free + 1].Bits = 0;
            return free + 1;
        }

        /// <summary>Returns the bits sent in a packet and frees its entry; 0 if there is none.</summary>
        public static ulong Take(SmallBitField[] table, byte packetId)
        {
            var key = packetId + 1UL;
            for (var i = 0; i < table.Length; i += 2)
            {
                if (table[i].Bits != key) continue;
                var bits = table[i + 1].Bits;
                table[i].Bits = 0;
                table[i + 1].Bits = 0;
                return bits;
            }
            return 0;
        }

        private static Func<object, T> BuildGetter<T>(Type owner, FieldInfo field)
        {
            var obj = Expression.Parameter(typeof(object), "obj");
            return Expression.Lambda<Func<object, T>>(Expression.Field(Expression.Convert(obj, owner), field), obj).Compile();
        }

        // DynamicMethod, because the field is readonly.
        private static Action<object, T> BuildSetter<T>(Type owner, FieldInfo field)
        {
            var method = new DynamicMethod("Set" + field.Name, null, new[] { typeof(object), typeof(T) }, owner.Module, true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, owner);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Ret);
            return (Action<object, T>)method.CreateDelegate(typeof(Action<object, T>));
        }

        private static Expression ClientDataDictionary(ParameterExpression group) =>
            Expression.Field(Expression.Field(Expression.Convert(group, GroupType), ServerDataField), ServerClientDataField);

        private static Func<object, Endpoint, object> BuildClientDataGetter()
        {
            var group = Expression.Parameter(typeof(object), "group");
            var endpoint = Expression.Parameter(typeof(Endpoint), "endpoint");
            var body = Expression.Convert(Expression.Property(ClientDataDictionary(group), "Item", endpoint), typeof(object));
            return Expression.Lambda<Func<object, Endpoint, object>>(body, group, endpoint).Compile();
        }

        private static Action<object, Endpoint, object> BuildClientDataAdder()
        {
            var group = Expression.Parameter(typeof(object), "group");
            var endpoint = Expression.Parameter(typeof(Endpoint), "endpoint");
            var data = Expression.Parameter(typeof(object), "data");
            var body = Expression.Call(ClientDataDictionary(group), "Add", null, endpoint, Expression.Convert(data, DataPerClientType));
            return Expression.Lambda<Action<object, Endpoint, object>>(body, group, endpoint, data).Compile();
        }

        private static Func<object, int> BuildPropertyCount()
        {
            var group = Expression.Parameter(typeof(object), "group");
            var body = Expression.Property(Expression.Field(Expression.Convert(group, GroupType), PropertiesField), "Count");
            return Expression.Lambda<Func<object, int>>(body, group).Compile();
        }

        private static Func<object, ulong> BuildDirtyGetter()
        {
            var data = Expression.Parameter(typeof(object), "data");
            var body = Expression.Field(Expression.Field(Expression.Convert(data, DataPerClientType), DirtyPropertiesField), nameof(SmallBitField.Bits));
            return Expression.Lambda<Func<object, ulong>>(body, data).Compile();
        }

        private static Action<object, ulong> BuildDirtySetter()
        {
            var method = new DynamicMethod("SetDirtyBits", null, new[] { typeof(object), typeof(ulong) }, DataPerClientType.Module, true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, DataPerClientType);
            il.Emit(OpCodes.Ldflda, DirtyPropertiesField);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, typeof(SmallBitField).GetField(nameof(SmallBitField.Bits)));
            il.Emit(OpCodes.Ret);
            return (Action<object, ulong>)method.CreateDelegate(typeof(Action<object, ulong>));
        }
    }
}
