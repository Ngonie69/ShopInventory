using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class CallTimings
    {
        public static Error LoadFailed(string message) =>
            Error.Failure("CallTimings.LoadFailed", message);
    }
}
