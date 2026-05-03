using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Batch
    {
        public static readonly Error SapDisabled =
            Error.Failure("Batch.SapDisabled", "SAP integration is disabled.");

        public static Error SapTimeout =>
            Error.Failure("Batch.SapTimeout", "Connection to SAP Service Layer timed out.");

        public static Error SapConnectionError(string message) =>
            Error.Failure("Batch.SapConnectionError", $"Unable to connect to SAP Service Layer. {message}");

        public static Error SearchFailed(string message) =>
            Error.Failure("Batch.SearchFailed", message);

        public static Error UpdateFailed(string message) =>
            Error.Failure("Batch.UpdateFailed", message);

        public static Error NotFound(int batchEntryId) =>
            Error.NotFound("Batch.NotFound", $"Batch with entry id '{batchEntryId}' was not found.");
    }
}