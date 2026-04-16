using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class PushNotification
    {
        public static Error SendFailed(string message) =>
            Error.Failure("PushNotification.SendFailed", message);

        public static readonly Error Unauthenticated =
            Error.Unauthorized("PushNotification.Unauthenticated", "User is not authenticated");

        public static Error RegistrationFailed(string message) =>
            Error.Failure("PushNotification.RegistrationFailed", message);
    }
}
