using ProtoBuf;

namespace SentisOptimisationsPlugin
{
  [ProtoContract]
  public enum MessageType : byte
  {
    SellReq,
    BuyReq,
    SetGridListReq,
    SelectGridReq,
    SetGridListResp,
    FixShip,
    ListForGuiReq,
    ListForGuiResp
  }
}
