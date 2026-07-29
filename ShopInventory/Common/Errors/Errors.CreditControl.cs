using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class CreditControl
    {
        public static readonly Error SapDisabled =
            Error.Failure("CreditControl.SapDisabled", "SAP integration is disabled, so credit limits cannot be read");

        public static Error ReviewFailed(string message) =>
            Error.Failure("CreditControl.ReviewFailed", message);
    }
}
