using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class SalesOrder
    {
        public static Error NotFound(int id) =>
            Error.NotFound("SalesOrder.NotFound", $"Sales order with ID {id} not found.");

        public static Error NotFoundByNumber(string orderNumber) =>
            Error.NotFound("SalesOrder.NotFound", $"Sales order '{orderNumber}' not found.");

        public static readonly Error Unauthorized =
            Error.Unauthorized("SalesOrder.Unauthorized", "User is not authenticated.");

        public static Error InvalidOperation(string message) =>
            Error.Validation("SalesOrder.InvalidOperation", message);

        public static Error ConcurrencyConflict =>
            Error.Conflict("SalesOrder.ConcurrencyConflict", "This order was modified by another user. Please reload and try again.");

        public static Error ApprovalFailed(string message) =>
            Error.Failure("SalesOrder.ApprovalFailed", $"Failed to approve sales order: {message}");

        public static Error SapError(string message) =>
            Error.Failure("SalesOrder.SapError", $"Failed to post to SAP: {message}");

        public static Error CreationFailed(string message) =>
            Error.Failure("SalesOrder.CreationFailed", message);
    }
}
