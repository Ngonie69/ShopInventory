using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class MarketBreakage
    {
        public static Error LoadFailed(string message) =>
            Error.Failure("MarketBreakage.LoadFailed", message);

        public static Error DecisionFailed(string message) =>
            Error.Failure("MarketBreakage.DecisionFailed", message);
    }
}
