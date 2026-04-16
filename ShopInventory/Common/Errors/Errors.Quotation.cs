using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Quotation
    {
        public static Error NotFound(int id) =>
            Error.NotFound("Quotation.NotFound", $"Quotation with ID {id} not found");

        public static Error NotFoundByNumber(string number) =>
            Error.NotFound("Quotation.NotFoundByNumber", $"Quotation '{number}' not found");

        public static Error NotFoundByDocEntry(int docEntry) =>
            Error.NotFound("Quotation.NotFoundByDocEntry", $"Quotation with DocEntry {docEntry} not found in SAP");

        public static Error CreationFailed(string message) =>
            Error.Failure("Quotation.CreationFailed", message);

        public static Error UpdateFailed(string message) =>
            Error.Failure("Quotation.UpdateFailed", message);

        public static Error ApprovalFailed(string message) =>
            Error.Failure("Quotation.ApprovalFailed", message);

        public static Error ConversionFailed(string message) =>
            Error.Failure("Quotation.ConversionFailed", message);

        public static Error DeleteFailed(string message) =>
            Error.Failure("Quotation.DeleteFailed", message);

        public static Error InvalidOperation(string message) =>
            Error.Validation("Quotation.InvalidOperation", message);
    }
}
