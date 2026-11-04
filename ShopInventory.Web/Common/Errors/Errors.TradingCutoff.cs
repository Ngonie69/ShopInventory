using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class TradingCutoff
    {
        public static Error Failed(string message) =>
            Error.Failure("TradingCutoff.Failed", message);
    }
}
