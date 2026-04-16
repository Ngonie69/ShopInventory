using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class TwoFactor
    {
        public static readonly Error Unauthenticated =
            Error.Unauthorized("TwoFactor.Unauthenticated", "User is not authenticated");

        public static Error SetupFailed(string message) =>
            Error.Failure("TwoFactor.SetupFailed", message);

        public static Error EnableFailed(string message) =>
            Error.Failure("TwoFactor.EnableFailed", message);

        public static Error DisableFailed(string message) =>
            Error.Failure("TwoFactor.DisableFailed", message);

        public static Error VerificationFailed(string message) =>
            Error.Failure("TwoFactor.VerificationFailed", message);

        public static Error RegenerateFailed(string message) =>
            Error.Failure("TwoFactor.RegenerateFailed", message);
    }
}
