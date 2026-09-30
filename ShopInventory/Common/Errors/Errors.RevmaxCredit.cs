using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class RevmaxCredit
    {
        public static readonly Error RevmaxDisabled = Error.Validation(
            "RevmaxCredit.RevmaxDisabled",
            "REVMax is retired here (Revmax:Enabled is off), so what it filed cannot be read back.");

        public static Error DeviceUnavailable(int docNum, string detail) => Error.Failure(
            "RevmaxCredit.DeviceUnavailable",
            $"REVMax did not say whether it holds invoice {docNum}: {detail}. Try again shortly.");

        public static Error NotFiscalised(int docNum) => Error.NotFound(
            "RevmaxCredit.NotFiscalised",
            $"REVMax holds no fiscal receipt for invoice {docNum}.");

        public static Error NotOurReceipt(int docNum, string detail) => Error.Conflict(
            "RevmaxCredit.NotOurReceipt",
            $"REVMax answered invoice {docNum} with a receipt that is not this invoice's: {detail}.");

        public static Error Incomplete(int docNum, string detail) => Error.Validation(
            "RevmaxCredit.Incomplete",
            $"REVMax's receipt for invoice {docNum} is missing what a credit note must cite: {detail}.");
    }
}
