using System.Collections.Generic;
using ProtoBuf;

namespace SentisOptimisationsPlugin
{
  [ProtoContract]
  public struct FillListResponse
  {
    [ProtoMember(1)]
    public long ShipyardId;
    [ProtoMember(2)]
    public List<GridForList> Grids;
  }
}
