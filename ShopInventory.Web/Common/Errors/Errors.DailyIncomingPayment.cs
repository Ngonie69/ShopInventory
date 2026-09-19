using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class DailyIncomingPayment
    {
        public static Error Failed(string message) =>
            Error.Failure("DailyIncomingPayment.Failed", message);
    }
}
