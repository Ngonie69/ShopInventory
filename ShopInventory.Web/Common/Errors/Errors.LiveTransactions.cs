using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class LiveTransactions
    {
        public static readonly Error LoadFailed =
            Error.Failure("LiveTransactions.LoadFailed", "The live transactions feed could not be read.");
    }
}
