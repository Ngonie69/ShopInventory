using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Auth
    {
        public static readonly Error LockedOut =
            Error.Failure("Auth.LockedOut", "Too many failed login attempts. Please try again later.");

        public static readonly Error InvalidCredentials =
            Error.Unauthorized("Auth.InvalidCredentials", "Invalid username or password");

        public static readonly Error InvalidRefreshToken =
            Error.Unauthorized("Auth.InvalidRefreshToken", "Invalid or expired refresh token");

        public static readonly Error Unauthenticated =
            Error.Unauthorized("Auth.Unauthenticated", "User is not authenticated");

        public static readonly Error UserNotFound =
            Error.NotFound("Auth.UserNotFound", "User not found");

        public static readonly Error InvalidPasskeyContext =
            Error.Validation("Auth.InvalidPasskeyContext", "Passkeys require a valid secure origin and relying party ID");

        public static readonly Error InvalidPasskeyOperation =
            Error.Unauthorized("Auth.InvalidPasskeyOperation", "Invalid or expired passkey operation");

        public static readonly Error PasskeyNotFound =
            Error.NotFound("Auth.PasskeyNotFound", "Passkey not found");

        public static Error InvalidRole(string role) =>
            Error.Validation("Auth.InvalidRole", $"Invalid role: {role}");

        public static readonly Error DuplicateUser =
            Error.Conflict("Auth.DuplicateUser", "Username or email already exists");

        public static Error RegistrationFailed(string message) =>
            Error.Failure("Auth.RegistrationFailed", message);

        public static Error PasskeyRegistrationFailed(string message) =>
            Error.Failure("Auth.PasskeyRegistrationFailed", message);

        public static Error PasskeyLoginFailed(string message) =>
            Error.Unauthorized("Auth.PasskeyLoginFailed", message);
    }
}
