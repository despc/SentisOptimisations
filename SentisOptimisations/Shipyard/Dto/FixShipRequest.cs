using ProtoBuf;

namespace SentisOptimisationsPlugin
{
  [ProtoContract]
  public struct FixShipRequest
  {
    [ProtoMember(1)]
    public long gridId;
  }
}
