using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Report
    {
        public static Error GenerationFailed(string message) =>
            Error.Failure("Report.GenerationFailed", message);

        public static readonly Error Timeout =
            Error.Failure("Report.Timeout", "Report generation timed out. Please try a smaller date range.");
    }
}
