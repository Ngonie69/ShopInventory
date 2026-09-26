using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class CountVariance
    {
        public static Error LoadFailed(string message) =>
            Error.Failure("CountVariance.LoadFailed", message);
    }
}
