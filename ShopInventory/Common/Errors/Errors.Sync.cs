using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Sync
    {
        public static Error TransactionNotFound(int id) =>
            Error.NotFound("Sync.TransactionNotFound", $"Transaction with ID {id} not found or not in expected state");

        public static Error ProcessingFailed(string message) =>
            Error.Failure("Sync.ProcessingFailed", message);

        public static Error ConnectionTestFailed(string message) =>
            Error.Failure("Sync.ConnectionTestFailed", message);

        public static readonly Error ItemTaxGroupSyncAlreadyRunning =
            Error.Conflict("Sync.ItemTaxGroupSyncAlreadyRunning", "An item tax group sync is already running. Try again after it finishes.");

        public static Error ItemTaxGroupReadFailed(string message) =>
            Error.Failure("Sync.ItemTaxGroupReadFailed", $"Could not read item tax groups from SAP; the stored ones are unchanged. {message}");

        public static readonly Error ItemUomWarmAlreadyRunning =
            Error.Conflict("Sync.ItemUomWarmAlreadyRunning", "An item UoM sync is already running. Try again after it finishes.");

        public static Error ItemUomWarmFailed(int pairs, string message) =>
            Error.Failure("Sync.ItemUomWarmFailed", $"Could not resolve any of the {pairs} item/UoM pair(s) from SAP; approvals will resolve them on demand. {message}");
    }
}
