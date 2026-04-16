using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Notification
    {
        public static Error NotFound(int id) =>
            Error.NotFound("Notification.NotFound", $"Notification with ID {id} not found");

        public static Error CreationFailed(string message) =>
            Error.Failure("Notification.CreationFailed", message);

        public static Error DeleteFailed(string message) =>
            Error.Failure("Notification.DeleteFailed", message);
    }
}
