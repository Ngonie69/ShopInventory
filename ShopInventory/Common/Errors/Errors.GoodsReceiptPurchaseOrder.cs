using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class GoodsReceiptPurchaseOrder
    {
        public static Error NotFoundByDocEntry(int docEntry) =>
            Error.NotFound("GoodsReceiptPurchaseOrder.NotFoundByDocEntry", $"Goods receipt PO with DocEntry {docEntry} not found in SAP");

        public static Error CreationFailed(string message) =>
            Error.Failure("GoodsReceiptPurchaseOrder.CreationFailed", message);

        public static Error LoadFailed(string message) =>
            Error.Failure("GoodsReceiptPurchaseOrder.LoadFailed", message);

        public static Error SapError(string message) =>
            Error.Failure("GoodsReceiptPurchaseOrder.SapError", message);
    }
}