using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class PurchaseRequest
    {
        public static Error LoadRequestsFailed(string message) =>
            Error.Failure("PurchaseRequest.LoadRequestsFailed", message);

        public static Error CreateRequestFailed(string message) =>
            Error.Failure("PurchaseRequest.CreateRequestFailed", message);
    }
}