using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class IncomingPayment
    {
        public static readonly Error SapDisabled =
            Error.Failure("IncomingPayment.SapDisabled", "SAP integration is disabled.");

        public static Error NotFound(int docEntry) =>
            Error.NotFound("IncomingPayment.NotFound", $"Incoming payment with DocEntry {docEntry} not found.");

        public static Error NotFoundByDocNum(int docNum) =>
            Error.NotFound("IncomingPayment.NotFound", $"Incoming payment with DocNum {docNum} not found.");

        public static Error ValidationFailed(string message) =>
            Error.Validation("IncomingPayment.ValidationFailed", message);

        public static Error SapTimeout =>
            Error.Failure("IncomingPayment.SapTimeout", "Connection to SAP Service Layer timed out or was aborted.");

        public static Error SapConnectionError(string message) =>
            Error.Failure("IncomingPayment.SapConnectionError", $"Unable to connect to SAP Service Layer. {message}");

        public static Error CreationFailed(string message) =>
            Error.Failure("IncomingPayment.CreationFailed", message);

        public static Error InvalidDateRange =>
            Error.Validation("IncomingPayment.InvalidDateRange", "From date must be less than or equal to To date.");

        public static Error CustomerCodeRequired =>
            Error.Validation("IncomingPayment.CustomerCodeRequired", "Customer code is required.");
    }
}
