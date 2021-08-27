using ProtoBuf;

namespace SentisOptimisationsPlugin
{
  [ProtoContract]
  public struct StartSellRequest
  {
    [ProtoMember(1)]
    public long ShipyardId;
    [ProtoMember(2)]
    public ulong SteamId;
    [ProtoMember(3)]
    public string ShipName;
  }
}
