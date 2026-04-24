using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class GoodsReceiptPurchaseOrder
    {
        public static Error LoadGoodsReceiptsFailed(string message) =>
            Error.Failure("GoodsReceiptPurchaseOrder.LoadGoodsReceiptsFailed", message);

        public static Error CreateGoodsReceiptFailed(string message) =>
            Error.Failure("GoodsReceiptPurchaseOrder.CreateGoodsReceiptFailed", message);
    }
}