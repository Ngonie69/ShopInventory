using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class Batch
    {
        public static Error SearchFailed(string message) =>
            Error.Failure("Batch.SearchFailed", message);

        public static Error UpdateFailed(string message) =>
            Error.Failure("Batch.UpdateFailed", message);
    }
}