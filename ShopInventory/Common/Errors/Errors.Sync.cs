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
    }
}
