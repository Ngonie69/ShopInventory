using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class PurchaseQuotation
    {
        public static Error NotFoundByDocEntry(int docEntry) =>
            Error.NotFound("PurchaseQuotation.NotFoundByDocEntry", $"Purchase quotation with DocEntry {docEntry} not found in SAP");

        public static Error CreationFailed(string message) =>
            Error.Failure("PurchaseQuotation.CreationFailed", message);

        public static Error LoadFailed(string message) =>
            Error.Failure("PurchaseQuotation.LoadFailed", message);

        public static Error SapError(string message) =>
            Error.Failure("PurchaseQuotation.SapError", message);
    }
}