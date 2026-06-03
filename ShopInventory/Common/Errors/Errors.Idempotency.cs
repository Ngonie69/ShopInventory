using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Idempotency
    {
        public static Error RequestInProgress(string operation) =>
            Error.Conflict(
                "Idempotency.RequestInProgress",
                $"An equivalent {operation} request is already in progress");

        public static Error RequestMismatch(string operation) =>
            Error.Conflict(
                "Idempotency.RequestMismatch",
                $"The idempotency key for this {operation} request was already used with different payload");
    }
}