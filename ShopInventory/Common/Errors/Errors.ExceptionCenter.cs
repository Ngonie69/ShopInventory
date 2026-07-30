using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class ExceptionCenter
    {
        public static Error ItemNotFound(string source, string itemKey) =>
            Error.NotFound("ExceptionCenter.ItemNotFound", $"Exception center item '{source}:{itemKey}' was not found.");

        public static Error RetryNotSupported(string source) =>
            Error.Validation("ExceptionCenter.RetryNotSupported", $"Retry is not supported for '{source}' items.");

        public static Error LoadFailed(string message) =>
            Error.Failure("ExceptionCenter.LoadFailed", message);

        public static Error UpdateFailed(string action, string message) =>
            Error.Failure($"ExceptionCenter.{action}Failed", message);
    }
}