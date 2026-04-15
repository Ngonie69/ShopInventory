using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Invoice
    {
        public static readonly Error SapDisabled =
            Error.Failure("Invoice.SapDisabled", "SAP integration is disabled.");

        public static Error NotFound(int docEntry) =>
            Error.NotFound("Invoice.NotFound", $"Invoice with DocEntry {docEntry} not found.");

        public static Error NotFoundByDocNum(int docNum) =>
            Error.NotFound("Invoice.NotFound", $"Invoice with DocNum {docNum} not found.");

        public static Error DuplicateVanSaleOrder(string vanSaleOrder, int docNum) =>
            Error.Conflict("Invoice.Duplicate", $"Invoice with U_Van_saleorder '{vanSaleOrder}' already exists in SAP (DocNum: {docNum}).");

        public static Error ValidationFailed(string message) =>
            Error.Validation("Invoice.ValidationFailed", message);

        public static Error BatchValidationFailed(string message) =>
            Error.Validation("Invoice.BatchValidationFailed", message);

        public static Error LockConflict =>
            Error.Conflict("Invoice.LockConflict", "Concurrent access detected - another operation is in progress for these items.");

        public static Error StockValidationFailed(string message) =>
            Error.Validation("Invoice.StockValidationFailed", message);

        public static Error SapTimeout =>
            Error.Failure("Invoice.SapTimeout", "Connection to SAP Service Layer timed out.");

        public static Error SapConnectionError(string message) =>
            Error.Failure("Invoice.SapConnectionError", $"Unable to connect to SAP Service Layer. {message}");

        public static Error CreationFailed(string message) =>
            Error.Failure("Invoice.CreationFailed", message);

        public static Error NoBatchesFound(string itemCode, string warehouseCode) =>
            Error.NotFound("Invoice.NoBatchesFound", $"No batches found for item '{itemCode}' in warehouse '{warehouseCode}'.");

        public static Error InvalidDateRange =>
            Error.Validation("Invoice.InvalidDateRange", "From date cannot be later than to date.");

        public static Error CustomerCodeRequired =>
            Error.Validation("Invoice.CustomerCodeRequired", "Customer code is required.");

        public static Error InvalidPageSize(int max) =>
            Error.Validation("Invoice.InvalidPageSize", $"Page size must be between 1 and {max}.");

        public static Error InvalidPage =>
            Error.Validation("Invoice.InvalidPage", "Page number must be at least 1.");

        public static Error PodExcluded(string cardName, string cardCode) =>
            Error.Validation("Invoice.PodExcluded", $"POD uploads are not required for {cardName} ({cardCode}).");
    }
}
