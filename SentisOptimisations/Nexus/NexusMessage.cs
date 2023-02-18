
using ProtoBuf;
using VRageMath;

namespace SentisOptimisationsPlugin
{
  [ProtoContract]
  public class NexusMessage
  {
    [ProtoMember(1)]
    public NexusMessageType Type;
    [ProtoMember(8)]
    public long ChatIdentityID;
    [ProtoMember(9)]
    public string Response;
    [ProtoMember(10)]
    public Color Color;
    [ProtoMember(11)]
    public string Sender;
    [ProtoMember(12)]
    public string Name;
    [ProtoMember(13)]
    public Vector3D Position;
    [ProtoMember(14)]
    public long EntityID;
    [ProtoMember(15)]
    public string Faction;
  }
}
