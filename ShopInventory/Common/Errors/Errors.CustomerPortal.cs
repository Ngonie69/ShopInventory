using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class CustomerPortal
    {
        public static readonly Error WeakPassword =
            Error.Validation("CustomerPortal.WeakPassword", "Password must be at least 8 characters and include uppercase, lowercase, digit, and special character");

        public static Error RegistrationFailed(string message) =>
            Error.Failure("CustomerPortal.RegistrationFailed", message);

        public static readonly Error DevOnlyEndpoint =
            Error.Failure("CustomerPortal.DevOnlyEndpoint", "This endpoint is only available in development");
    }
}
