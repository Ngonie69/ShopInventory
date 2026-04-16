using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Email
    {
        public static Error SendFailed(string message) =>
            Error.Failure("Email.SendFailed", message);

        public static Error QueueFailed(string message) =>
            Error.Failure("Email.QueueFailed", message);

        public static Error ProcessingFailed(string message) =>
            Error.Failure("Email.ProcessingFailed", message);
    }
}
