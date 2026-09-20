using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class StockWriteOff
    {
        public static Error LoadFailed(string message) =>
            Error.Failure("StockWriteOff.LoadFailed", message);

        public static Error PostFailed(string message) =>
            Error.Failure("StockWriteOff.PostFailed", message);
    }
}
