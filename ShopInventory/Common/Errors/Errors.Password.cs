using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Password
    {
        public static readonly Error InvalidToken =
            Error.Validation("Password.InvalidToken", "Invalid or expired password reset token");

        public static Error ResetFailed(string message) =>
            Error.Failure("Password.ResetFailed", message);

        public static readonly Error Unauthenticated =
            Error.Unauthorized("Password.Unauthenticated", "User is not authenticated");

        public static Error UserNotFound(string userId) =>
            Error.NotFound("Password.UserNotFound", $"User with ID '{userId}' not found");

        public static Error ChangeFailed(string message) =>
            Error.Failure("Password.ChangeFailed", message);

        public static Error CredentialsNotFound =
            Error.NotFound("Password.CredentialsNotFound", "Credentials not found");

        public static Error UpdateFailed(string message) =>
            Error.Failure("Password.UpdateFailed", message);
    }
}
