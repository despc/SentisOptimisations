using ProtoBuf;

namespace Sentis
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
        ListForGuiResp,
        CancelSellReq,
        SyncConvert
    }
}