using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class PurchaseRequest
    {
        public static Error NotFoundByDocEntry(int docEntry) =>
            Error.NotFound("PurchaseRequest.NotFoundByDocEntry", $"Purchase request with DocEntry {docEntry} not found in SAP");

        public static Error CreationFailed(string message) =>
            Error.Failure("PurchaseRequest.CreationFailed", message);

        public static Error LoadFailed(string message) =>
            Error.Failure("PurchaseRequest.LoadFailed", message);

        public static Error SapError(string message) =>
            Error.Failure("PurchaseRequest.SapError", message);
    }
}