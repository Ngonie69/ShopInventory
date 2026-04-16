using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Payment
    {
        public static Error NotFound(int id) =>
            Error.NotFound("Payment.NotFound", $"Payment transaction with ID {id} not found");

        public static Error InitiationFailed(string message) =>
            Error.Failure("Payment.InitiationFailed", message);

        public static Error CancellationFailed(string message) =>
            Error.Failure("Payment.CancellationFailed", message);

        public static Error RefundFailed(string message) =>
            Error.Failure("Payment.RefundFailed", message);

        public static Error CallbackFailed(string message) =>
            Error.Failure("Payment.CallbackFailed", message);
    }
}
