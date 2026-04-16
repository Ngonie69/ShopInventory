using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Webhook
    {
        public static Error NotFound(int id) =>
            Error.NotFound("Webhook.NotFound", $"Webhook with ID {id} not found");

        public static Error CreationFailed(string message) =>
            Error.Failure("Webhook.CreationFailed", message);

        public static Error UpdateFailed(string message) =>
            Error.Failure("Webhook.UpdateFailed", message);

        public static Error TestFailed(string message) =>
            Error.Failure("Webhook.TestFailed", message);
    }
}
