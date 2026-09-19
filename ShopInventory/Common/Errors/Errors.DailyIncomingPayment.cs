using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class DailyIncomingPayment
    {
        public static Error NotFound(int id) =>
            Error.NotFound("DailyIncomingPayment.NotFound", $"Daily incoming payment {id} was not found.");

        public static Error UnknownAccount(string code) =>
            Error.Validation("DailyIncomingPayment.UnknownAccount", $"G/L account {code} does not exist in SAP.");

        public static Error InactiveAccount(string code) =>
            Error.Validation("DailyIncomingPayment.InactiveAccount", $"G/L account {code} is not an active postable account in SAP.");

        public static Error SapUnavailable(string detail) =>
            Error.Failure(
                "DailyIncomingPayment.SapUnavailable",
                $"The G/L accounts could not be checked in SAP, so nothing was saved. {detail}");
    }
}
