using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class InventoryTransfer
    {
        public static readonly Error SapDisabled =
            Error.Failure("InventoryTransfer.SapDisabled", "SAP integration is disabled.");

        public static Error NotFound(int docEntry) =>
            Error.NotFound("InventoryTransfer.NotFound", $"Inventory transfer with DocEntry {docEntry} not found.");

        public static Error TransferRequestNotFound(int docEntry) =>
            Error.NotFound("InventoryTransfer.TransferRequestNotFound", $"Transfer request with DocEntry {docEntry} not found.");

        public static Error ValidationFailed(string message) =>
            Error.Validation("InventoryTransfer.ValidationFailed", message);

        public static Error InvalidWarehouse(string message) =>
            Error.Validation("InventoryTransfer.InvalidWarehouse", message);

        public static Error InsufficientStock(string message) =>
            Error.Validation("InventoryTransfer.InsufficientStock", message);

        public static Error NegativeStock(string message) =>
            Error.Validation("InventoryTransfer.NegativeStock", $"Transfer rejected: {message}");

        public static Error SapTimeout =>
            Error.Failure("InventoryTransfer.SapTimeout", "Connection to SAP Service Layer timed out or was aborted.");

        public static Error SapConnectionError(string message) =>
            Error.Failure("InventoryTransfer.SapConnectionError", $"Unable to connect to SAP Service Layer. {message}");

        public static Error CreationFailed(string message) =>
            Error.Failure("InventoryTransfer.CreationFailed", message);

        public static Error InvalidDateFormat(string paramName) =>
            Error.Validation("InventoryTransfer.InvalidDateFormat", $"Invalid {paramName} format. Use yyyy-MM-dd format.");

        public static Error InvalidDateRange =>
            Error.Validation("InventoryTransfer.InvalidDateRange", "fromDate cannot be greater than toDate.");

        public static Error WarehouseCodeRequired =>
            Error.Validation("InventoryTransfer.WarehouseCodeRequired", "Warehouse code is required.");

        public static Error InvalidOperation(string message) =>
            Error.Validation("InventoryTransfer.InvalidOperation", message);
    }
}
