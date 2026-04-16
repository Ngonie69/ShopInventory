using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Revmax
    {
        public static Error DeviceError(string message) =>
            Error.Failure("Revmax.DeviceError", message);

        public static Error InvoiceNotFound(string invoiceNumber) =>
            Error.NotFound("Revmax.InvoiceNotFound", $"Fiscal invoice '{invoiceNumber}' not found");

        public static readonly Error InvalidRequest =
            Error.Validation("Revmax.InvalidRequest", "Request body is required");

        public static readonly Error InvalidLicense =
            Error.Validation("Revmax.InvalidLicense", "License field is required");

        public static readonly Error InvalidItems =
            Error.Validation("Revmax.InvalidItems", "Items XML is required");

        public static Error TransactionFailed(string message) =>
            Error.Failure("Revmax.TransactionFailed", message);
    }
}
