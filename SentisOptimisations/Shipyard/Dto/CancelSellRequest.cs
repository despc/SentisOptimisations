using ProtoBuf;

namespace SentisOptimisationsPlugin
{
  [ProtoContract]
  public struct CancelSellRequest
  {
    [ProtoMember(1)]
    public long ShipyardId;
    [ProtoMember(2)]
    public ulong SteamId;
  }
}
