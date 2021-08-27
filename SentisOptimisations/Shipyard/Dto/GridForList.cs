using ProtoBuf;

namespace SentisOptimisationsPlugin
{
    [ProtoContract]
    public struct GridForList
    {
        [ProtoMember(1)]
        public string GridName;
        [ProtoMember(2)]
        public long ShipyardIdWithSelection;
    }
}