using ProtoBuf;

namespace SentisOptimisationsPlugin.AnomalyZone
{
  [ProtoContract]
  public struct DrawSphereRequest
  {
    [ProtoMember(1)]
    public long blockId;
    [ProtoMember(2)]
    public string color;
  }
}
