using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class ExceptionCenter
    {
        public static Error LoadFailed(string message) =>
            Error.Failure("ExceptionCenter.LoadFailed", message);

        public static Error RetryFailed(string message) =>
            Error.Failure("ExceptionCenter.RetryFailed", message);

        public static Error AcknowledgeFailed(string message) =>
            Error.Failure("ExceptionCenter.AcknowledgeFailed", message);

        public static Error AssignFailed(string message) =>
            Error.Failure("ExceptionCenter.AssignFailed", message);
    }
}