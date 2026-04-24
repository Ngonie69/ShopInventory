using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class PurchaseInvoice
    {
        public static Error NotFoundByDocEntry(int docEntry) =>
            Error.NotFound("PurchaseInvoice.NotFoundByDocEntry", $"A/P invoice with DocEntry {docEntry} not found in SAP");

        public static Error CreationFailed(string message) =>
            Error.Failure("PurchaseInvoice.CreationFailed", message);

        public static Error LoadFailed(string message) =>
            Error.Failure("PurchaseInvoice.LoadFailed", message);

        public static Error SapError(string message) =>
            Error.Failure("PurchaseInvoice.SapError", message);
    }
}