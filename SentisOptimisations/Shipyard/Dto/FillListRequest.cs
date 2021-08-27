using ProtoBuf;

namespace SentisOptimisationsPlugin
{
  [ProtoContract]
  public struct FillListRequest
  {
    [ProtoMember(1)]
    public long ShipyardId;
    [ProtoMember(2)]
    public ulong SteamId;
  }
}
