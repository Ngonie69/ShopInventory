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

        /// <summary>
        /// SAP did not answer one of the reads a statement cannot be shown without, even after a
        /// retry. Nothing is still running and nothing was kept, so unlike <see cref="Timeout"/> it
        /// does not suggest the statement is on its way.
        /// </summary>
        public static readonly Error SapNotAnswering =
            Error.Failure(
                "Statement.SapNotAnswering",
                "Our accounting system didn't answer in time, so this statement couldn't be built. Please try again in a minute.");

        public static readonly Error Timeout =
            Error.Failure(
                "Statement.Timeout",
                "This statement is taking longer than expected to build. It is still being prepared — please try again in a moment, or use a shorter date range.");
    }
}
