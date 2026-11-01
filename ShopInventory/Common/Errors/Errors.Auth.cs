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

        // The second step of a two-factor sign-in. Each one is an Unauthorized error (403) like
        // InvalidCredentials, which it replaced, because the till treats any 401/403 here as "code not
        // accepted" and keeps the challenge open. Only someone holding a challenge token, which takes
        // the right password, can tell these apart.

        public static readonly Error TwoFactorChallengeExpired =
            Error.Unauthorized(
                "Auth.TwoFactorChallengeExpired",
                "This sign-in has expired or was replaced by a newer one. Sign in with your password again.");

        public static readonly Error TwoFactorCodeInvalid =
            Error.Unauthorized(
                "Auth.TwoFactorCodeInvalid",
                "That code is not correct. Enter the code your authenticator app shows now, and check the phone's clock is set automatically.");

        public static readonly Error TwoFactorCodeAlreadyUsed =
            Error.Unauthorized(
                "Auth.TwoFactorCodeAlreadyUsed",
                "That code has already been used. Wait for your authenticator app to show a new one.");

        public static readonly Error TwoFactorBackupCodeInvalid =
            Error.Unauthorized(
                "Auth.TwoFactorBackupCodeInvalid",
                "That backup code is not valid. Each backup code works only once.");

        public static readonly Error TwoFactorNoBackupCodes =
            Error.Unauthorized(
                "Auth.TwoFactorNoBackupCodes",
                "This account has no backup codes left. Use your authenticator app, or ask an administrator to reset two-factor.");

        public static readonly Error TwoFactorNotConfigured =
            Error.Unauthorized(
                "Auth.TwoFactorNotConfigured",
                "Two-factor authentication is not set up properly on this account. Ask an administrator to reset it.");

        public static Error TwoFactorLockedOut(int minutes) =>
            Error.Unauthorized(
                "Auth.TwoFactorLockedOut",
                minutes <= 1
                    ? "Too many wrong codes. Wait a minute, then sign in with your password again."
                    : $"Too many wrong codes. Wait {minutes} minutes, then sign in with your password again.");

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

        public static readonly Error AppVersionBlocked =
            Error.Unauthorized("Auth.AppVersionBlocked", "This app version is no longer supported. Please update to continue.");

        public static readonly Error InvalidAppVersionMetadata =
            Error.Validation("Auth.InvalidAppVersionMetadata", "Android app requests must include valid X-App-Platform and X-App-Version headers.");

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
