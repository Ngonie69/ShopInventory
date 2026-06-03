using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Statement
    {
        public static Error CustomerNotFound(string cardCode) =>
            Error.NotFound("Statement.CustomerNotFound", $"Customer with code '{cardCode}' not found");

        public static Error RetrievalFailed(string message) =>
            Error.Failure("Statement.RetrievalFailed", message);

        public static Error GenerationFailed(string message) =>
            Error.Failure("Statement.GenerationFailed", message);
    }
}
