using ProtoBuf;

namespace SentisOptimisationsPlugin.AnomalyZone
{
  [ProtoContract]
  public enum MessageType : byte
  {
    DrawSphere,
    RemoveSphere
  }
}
