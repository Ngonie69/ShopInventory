using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class StockWriteOff
    {
        public static readonly Error SapDisabled =
            Error.Failure("StockWriteOff.SapDisabled", "SAP integration is disabled, so nothing can be written off.");

        public static Error NotFound(int id) =>
            Error.NotFound("StockWriteOff.NotFound", $"Write-off {id} was not found.");

        public static readonly Error UserNotFound =
            Error.NotFound("StockWriteOff.UserNotFound", "Your account could not be found.");

        public static readonly Error WarehouseRequired =
            Error.Validation("StockWriteOff.WarehouseRequired", "Choose the warehouse the stock is leaving.");

        public static Error UnknownWarehouse(string warehouseCode) =>
            Error.Validation("StockWriteOff.UnknownWarehouse",
                $"SAP has no warehouse '{warehouseCode}'.");

        public static Error UnknownReason(string reason) =>
            Error.Validation("StockWriteOff.UnknownReason",
                $"'{reason}' is not one of the reasons a write-off may be given. Pick one from the list.");

        public static readonly Error NothingToWriteOff =
            Error.Validation("StockWriteOff.NothingToWriteOff", "Add at least one line before writing anything off.");

        public static Error LinesInvalid(string message) =>
            Error.Validation("StockWriteOff.LinesInvalid", message);

        public static Error NotActionable(string status) =>
            Error.Conflict("StockWriteOff.NotActionable", $"This write-off is {status} and can no longer be posted.");

        public static readonly Error PostInProgress =
            Error.Conflict("StockWriteOff.PostInProgress",
                "This write-off is already being posted to SAP. Wait for it to finish, then refresh.");

        public static readonly Error DuplicateRequestFromAnotherUser =
            Error.Conflict("StockWriteOff.DuplicateRequest", "This write-off id has already been used by another account.");

        public static Error InsufficientStock(string message) =>
            Error.Validation("StockWriteOff.InsufficientStock", message);

        public static Error PostFailed(string message) =>
            Error.Failure("StockWriteOff.PostFailed", message);
    }
}
