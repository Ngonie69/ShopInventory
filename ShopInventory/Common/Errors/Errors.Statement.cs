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

        public static readonly Error Timeout =
            Error.Failure(
                "Statement.Timeout",
                "This statement is taking longer than expected to build. It is still being prepared — please try again in a moment, or use a shorter date range.");
    }
}
