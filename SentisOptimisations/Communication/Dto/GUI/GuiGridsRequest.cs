using ProtoBuf;

namespace SentisOptimisationsPlugin
{
  [ProtoContract]
  public struct GuiGridsRequest
  {
    [ProtoMember(1)]
    public ulong SteamId;
  }
}
