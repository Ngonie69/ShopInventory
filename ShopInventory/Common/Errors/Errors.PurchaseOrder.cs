using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class PurchaseOrder
    {
        public static Error NotFound(int id) =>
            Error.NotFound("PurchaseOrder.NotFound", $"Purchase order with ID {id} not found");

        public static Error NotFoundByNumber(string orderNumber) =>
            Error.NotFound("PurchaseOrder.NotFoundByNumber", $"Purchase order '{orderNumber}' not found");

        public static Error NotFoundByDocEntry(int docEntry) =>
            Error.NotFound("PurchaseOrder.NotFoundByDocEntry", $"Purchase order with DocEntry {docEntry} not found in SAP");

        public static Error CreationFailed(string message) =>
            Error.Failure("PurchaseOrder.CreationFailed", message);

        public static Error UpdateFailed(string message) =>
            Error.Failure("PurchaseOrder.UpdateFailed", message);

        public static Error ApprovalFailed(string message) =>
            Error.Failure("PurchaseOrder.ApprovalFailed", message);

        public static Error ReceiveFailed(string message) =>
            Error.Failure("PurchaseOrder.ReceiveFailed", message);

        public static Error DeleteFailed(string message) =>
            Error.Failure("PurchaseOrder.DeleteFailed", message);

        public static Error UploadFailed(string message) =>
            Error.Failure("PurchaseOrder.UploadFailed", message);

        public static Error UploadValidationFailed(string message) =>
            Error.Validation("PurchaseOrder.UploadValidationFailed", message);

        public static readonly Error SapDisabled =
            Error.Failure("PurchaseOrder.SapDisabled", "SAP integration is disabled");

        public static Error SapError(string message) =>
            Error.Failure("PurchaseOrder.SapError", message);
    }
}
